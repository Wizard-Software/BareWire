using System.Globalization;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Google.PubSub.Internal;
using BareWire.Transport.Google.PubSub.Topology;
using Google.Api.Gax.Grpc;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.Google.PubSub;

/// <summary>
/// Google Cloud Pub/Sub transport adapter. Implements producer (<see cref="SendBatchAsync"/>),
/// topology deployment (<see cref="DeployTopologyAsync"/>), and lifecycle management.
/// Consumer side (<see cref="ConsumeAsync"/>, <see cref="SettleAsync"/>) is implemented in
/// <c>PubSubTransportAdapter.Consumer.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Uses single, long-lived, thread-safe <see cref="PublisherServiceApiClient"/> and
/// <see cref="SubscriberServiceApiClient"/> instances constructed lazily on first use under a
/// <see cref="SemaphoreSlim"/> double-check lock (pattern mirrored from SQS adapter).
/// </para>
/// <para>
/// <b>Producer body:</b> Pub/Sub <c>PubsubMessage.Data</c> is a <c>ByteString</c> (raw bytes).
/// <c>OutboundMessage.Body</c> (<see cref="ReadOnlyMemory{T}"/>) is copied via
/// <c>ByteString.CopyFrom(span)</c> at the SDK boundary — a single allocation per message,
/// unavoidable at the transport boundary (acknowledged ADR-003 deviation, same as SQS/Kafka).
/// </para>
/// <para>
/// <b>Capabilities note:</b>
/// <see cref="TransportCapabilities.OrderingKeys"/> is declared because Pub/Sub natively supports
/// ordering keys; full BareWire-level mapping (CorrelationId → ordering key) implemented (R5.2).
/// <see cref="TransportCapabilities.DlqNative"/> is active: <c>DeadLetterPolicy</c> is wired onto
/// subscriptions during <see cref="DeployTopologyAsync"/> when <c>bw.pubsub.dead-letter-topic</c>
/// and <c>bw.pubsub.max-delivery-attempts</c> topology arguments are present (R5.3).
/// <see cref="TransportCapabilities.FlowControl"/> is declared because
/// <c>MaxOutstandingMessages</c>/<c>MaxOutstandingBytes</c> map 1:1 to BareWire
/// <c>FlowControlOptions</c>, making Pub/Sub's flow control model directly equivalent.
/// </para>
/// <para>
/// <b>Batch chunking (PERF-1):</b> Pub/Sub hard-limits <c>PublishRequest</c> to 1000 messages
/// or ~10 MB. <see cref="SendBatchAsync"/> uses index-based chunking with a ~9.5 MB safety
/// margin that accounts for attribute bytes + ordering key bytes, not just body length.
/// </para>
/// </remarks>
internal sealed partial class PubSubTransportAdapter : ITransportAdapter, IAsyncDisposable
{
    // Pub/Sub hard limits per PublishRequest.
    private const int MaxBatchCount = 1000;

    // Safety margin: 9.5 MB (not 10 MB) to account for protobuf request envelope overhead (PERF-1).
    private const long MaxBatchBytes = 9_500_000L;

    private readonly PubSubTransportOptions _options;
    private readonly ILogger<PubSubTransportAdapter> _logger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    // Low-level API clients — abstract classes with virtual methods, mockable by NSubstitute.
    private PublisherServiceApiClient? _publisher;
    private SubscriberServiceApiClient? _subscriber;
    private bool _disposed;

    // Monotonic delivery tag counter (shared between producer and consumer partial classes).
    private ulong _deliveryTagCounter;

    internal PubSubTransportAdapter(
        PubSubTransportOptions options,
        ILogger<PubSubTransportAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Internal test constructor that injects pre-built <see cref="PublisherServiceApiClient"/>
    /// and <see cref="SubscriberServiceApiClient"/> instances.
    /// Skips <c>EnsureClientsAsync</c> by pre-populating the client fields.
    /// Available only to assemblies with <c>InternalsVisibleTo</c>.
    /// </summary>
    internal PubSubTransportAdapter(
        PubSubTransportOptions options,
        ILogger<PubSubTransportAdapter> logger,
        PublisherServiceApiClient publisher,
        SubscriberServiceApiClient subscriber)
        : this(options, logger)
    {
        _publisher = publisher;
        _subscriber = subscriber;
    }

    /// <inheritdoc />
    public string TransportName => "Google.PubSub";

    /// <inheritdoc />
    /// <remarks>
    /// <b>Capabilities:</b>
    /// <list type="bullet">
    /// <item><term><see cref="TransportCapabilities.OrderingKeys"/></term><description>Pub/Sub supports ordering keys natively; full CorrelationId mapping implemented (R5.2).</description></item>
    /// <item><term><see cref="TransportCapabilities.BatchReceive"/></term><description>Pull supports <c>maxMessages</c> &gt; 1.</description></item>
    /// <item><term><see cref="TransportCapabilities.DlqNative"/></term><description><c>DeadLetterPolicy</c> is wired onto subscriptions during topology deployment when <c>bw.pubsub.dead-letter-topic</c> is specified (R5.3).</description></item>
    /// <item><term><see cref="TransportCapabilities.FlowControl"/></term><description>MaxOutstandingMessages/MaxOutstandingBytes map 1:1 to BareWire FlowControlOptions.</description></item>
    /// </list>
    /// </remarks>
    public TransportCapabilities Capabilities =>
        TransportCapabilities.OrderingKeys |
        TransportCapabilities.BatchReceive |
        TransportCapabilities.DlqNative |
        TransportCapabilities.FlowControl;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (messages.Count == 0)
        {
            return Array.Empty<SendResult>();
        }

        try
        {
            await EnsureClientsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new BareWireTransportException(
                message: "Failed to establish Google Cloud Pub/Sub client connection before sending batch.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }

        var results = new SendResult[messages.Count];

        // Group messages by RoutingKey (= topic name).
        // Materialise each group into a list for O(1) indexed slicing (PERF-1).
        var groups = new Dictionary<string, List<(int OriginalIndex, OutboundMessage Message)>>(
            StringComparer.Ordinal);

        for (int i = 0; i < messages.Count; i++)
        {
            OutboundMessage msg = messages[i];

            if (!groups.TryGetValue(msg.RoutingKey, out List<(int, OutboundMessage)>? group))
            {
                group = [];
                groups[msg.RoutingKey] = group;
            }

            group.Add((i, msg));
        }

        foreach ((string topicName, List<(int OriginalIndex, OutboundMessage Message)> group) in groups)
        {
            var topicResourceName = TopicName.FromProjectTopic(_options.ProjectId, topicName);

            // Chunk the group using index-based slicing: max 1000 messages per chunk,
            // max ~9.5 MB per chunk (attribute-inclusive, PERF-1).
            int i = 0;
            int groupCount = group.Count;
            int chunkStart = 0; // tracks original index offset for result mapping

            while (i < groupCount)
            {
                // PERF-2: pre-allocate with bounded capacity (mirror SQS PERF-2).
                var chunk = new List<PubsubMessage>(Math.Min(MaxBatchCount, groupCount - i));
                var chunkOriginalIndices = new List<int>(chunk.Capacity);
                long chunkBytes = 0;

                while (i < groupCount && chunk.Count < MaxBatchCount)
                {
                    (int originalIndex, OutboundMessage outbound) = group[i];

                    // Resolve ordering key: BW-OrderingKey → correlation-id → empty (R5.2, PubSubOrderingKeyResolver).
                    string orderingKey = PubSubOrderingKeyResolver.Resolve(outbound.Headers);

                    // D3: when ordering is required, a missing key breaks ordering guarantees — fail fast.
                    // Message contains only header NAMES, never values (SEC-4).
                    if (_options.EnableMessageOrdering && string.IsNullOrEmpty(orderingKey))
                    {
                        throw new BareWireTransportException(
                            message: $"Cannot send message to Pub/Sub topic '{topicName}' with message ordering " +
                                     $"enabled: no ordering key resolved. Provide a non-empty " +
                                     $"'{PubSubHeaderMapper.OrderingKeyHeader}' or " +
                                     $"'{PubSubOrderingKeyResolver.CorrelationIdHeader}' header.",
                            transportName: TransportName,
                            endpointAddress: null);
                    }

                    // PERF-1: estimate inclusive bytes (body + attributes + ordering key).
                    long msgBytes = PubSubHeaderMapper.EstimateMessageBytes(
                        outbound.Body.Length, outbound.Headers, orderingKey);

                    // Close chunk if adding this message would exceed the byte budget
                    // (but always include at least one message to avoid infinite loop).
                    if (chunk.Count > 0 && chunkBytes + msgBytes > MaxBatchBytes)
                    {
                        break;
                    }

                    // SEC-1: validate attribute limits before SDK call.
                    Dictionary<string, string> attributes = PubSubHeaderMapper.MapOutbound(outbound.Headers);

                    chunk.Add(new PubsubMessage
                    {
                        Data = ByteString.CopyFrom(outbound.Body.Span),
                        Attributes = { attributes },
                        OrderingKey = orderingKey,
                    });
                    chunkOriginalIndices.Add(originalIndex);
                    chunkBytes += msgBytes;
                    i++;
                }

                PublishResponse response;
                try
                {
                    response = await _publisher!
                        .PublishAsync(topicResourceName, chunk, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (RpcException rpc) when (rpc.StatusCode is StatusCode.FailedPrecondition or StatusCode.InvalidArgument)
                {
                    // D4: ordering key rejected by the broker. With the low-level API there is no
                    // client-side ResumePublish; subsequent ordered publishes for the same key are
                    // blocked server-side until a successful publish. Surface a clear BareWire error
                    // so the caller knows this is an ordering failure, not a generic publish error.
                    // Message names the topic and RPC status enum only — never the ordering key value (SEC-4).
                    throw new BareWireTransportException(
                        message: $"Failed to publish ordered message batch to Pub/Sub topic '{topicName}' " +
                                 $"(ordering key rejected: {rpc.StatusCode}). Subsequent ordered publishes for " +
                                 $"the same key are blocked until a successful publish.",
                        transportName: TransportName,
                        endpointAddress: null,
                        innerException: rpc);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new BareWireTransportException(
                        message: $"Failed to publish message batch to Pub/Sub topic '{topicName}'.",
                        transportName: TransportName,
                        endpointAddress: null,
                        innerException: ex);
                }

                // Map PublishResponse.MessageIds positionally to results (per-message ordering).
                for (int j = 0; j < chunk.Count; j++)
                {
                    int originalIndex = chunkOriginalIndices[j];
                    string messageId = j < response.MessageIds.Count ? response.MessageIds[j] : string.Empty;
                    bool confirmed = !string.IsNullOrEmpty(messageId);

                    results[originalIndex] = new SendResult(
                        IsConfirmed: confirmed,
                        DeliveryTag: (ulong)(originalIndex + 1));

                    if (confirmed)
                    {
                        LogMessageSent(topicName, messageId);
                    }
                    else
                    {
                        LogMessageSendFailed(topicName);
                    }
                }

                chunkStart += chunk.Count;
            }
        }

        return results;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Creates Pub/Sub topics from <see cref="TopologyDeclaration.Exchanges"/> (one topic per
    /// exchange name) and subscriptions from <see cref="TopologyDeclaration.Queues"/> (one
    /// subscription per queue name). The subscription's source topic is resolved from
    /// <see cref="TopologyDeclaration.ExchangeQueueBindings"/> — the first binding whose
    /// <c>QueueName</c> matches the queue name is used.
    /// <para>
    /// <b>Idempotence:</b> <c>AlreadyExists</c> gRPC status is swallowed on topic and
    /// subscription creation — it is safe to call this method multiple times.
    /// </para>
    /// <para>
    /// <b>DLQ wiring (R5.3):</b> When a queue declares <c>bw.pubsub.dead-letter-topic</c> and
    /// <c>bw.pubsub.max-delivery-attempts</c> topology arguments, the dead-letter topic is created
    /// idempotently and <c>DeadLetterPolicy</c> is applied to the subscription before it is
    /// registered with Pub/Sub. The subscription's service account must hold
    /// <c>roles/pubsub.publisher</c> on the dead-letter topic for the broker to route messages.
    /// </para>
    /// </remarks>
    public async Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureClientsAsync(cancellationToken).ConfigureAwait(false);

        // Deploy topics (exchanges → topics).
        foreach (ExchangeDeclaration exchange in topology.Exchanges)
        {
            var topicName = TopicName.FromProjectTopic(_options.ProjectId, exchange.Name);

            try
            {
                await _publisher!.CreateTopicAsync(topicName, cancellationToken)
                    .ConfigureAwait(false);
                LogTopicCreated(exchange.Name);
            }
            catch (RpcException rpc) when (rpc.StatusCode == StatusCode.AlreadyExists)
            {
                // Idempotent — topic already exists; skip.
                LogTopicAlreadyExists(exchange.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new TopologyDeploymentException(
                    topologyElement: exchange.Name,
                    transportName: TransportName,
                    brokerError: ex.Message,
                    endpointAddress: null,
                    innerException: ex);
            }
        }

        // Build a binding lookup: queue name → source topic name (first binding wins).
        var queueToTopic = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ExchangeQueueBinding binding in topology.ExchangeQueueBindings)
        {
            if (!queueToTopic.ContainsKey(binding.QueueName))
            {
                queueToTopic[binding.QueueName] = binding.ExchangeName;
                LogBindingMapped(binding.ExchangeName, binding.QueueName);
            }
        }

        // Deploy subscriptions (queues → subscriptions).
        foreach (QueueDeclaration queue in topology.Queues)
        {
            PubSubResourceSpec spec = PubSubTopologyArguments.Parse(queue);

            // Resolve the source topic from bindings; fall back to queue name as topic name.
            string sourceTopic = queueToTopic.TryGetValue(queue.Name, out string? topic)
                ? topic
                : queue.Name;

            var subscriptionName = SubscriptionName.FromProjectSubscription(_options.ProjectId, queue.Name);
            var topicName = TopicName.FromProjectTopic(_options.ProjectId, sourceTopic);

            bool orderingEnabled = spec.OrderingEnabled || _options.EnableMessageOrdering;

            var subscription = new Subscription
            {
                SubscriptionName = subscriptionName,
                TopicAsTopicName = topicName,
                AckDeadlineSeconds = (int)spec.AckDeadline.TotalSeconds,
                EnableMessageOrdering = orderingEnabled,
            };

            // R5.3: When a dead-letter topic is declared, create it idempotently and wire
            // DeadLetterPolicy onto the subscription before calling CreateSubscriptionAsync.
            if (!string.IsNullOrEmpty(spec.DeadLetterTopic))
            {
                var deadLetterTopicName = TopicName.FromProjectTopic(_options.ProjectId, spec.DeadLetterTopic);

                // The DLQ topic must exist before DeadLetterPolicy references it.
                // Same idempotent pattern as source topic creation above.
                try
                {
                    await _publisher!.CreateTopicAsync(deadLetterTopicName, cancellationToken)
                        .ConfigureAwait(false);
                    LogDeadLetterTopicCreated(spec.DeadLetterTopic, queue.Name);
                }
                catch (RpcException rpc) when (rpc.StatusCode == StatusCode.AlreadyExists)
                {
                    // Idempotent — DLQ topic already exists; skip.
                    LogDeadLetterTopicAlreadyExists(spec.DeadLetterTopic);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new TopologyDeploymentException(
                        topologyElement: spec.DeadLetterTopic,
                        transportName: TransportName,
                        brokerError: ex.Message,
                        endpointAddress: null,
                        innerException: ex);
                }

                subscription.DeadLetterPolicy = new DeadLetterPolicy
                {
                    DeadLetterTopic = deadLetterTopicName.ToString(),
                    MaxDeliveryAttempts = spec.MaxDeliveryAttempts,
                };
                LogDeadLetterPolicyApplied(queue.Name, spec.DeadLetterTopic, spec.MaxDeliveryAttempts);
            }

            try
            {
                await _subscriber!.CreateSubscriptionAsync(subscription, cancellationToken)
                    .ConfigureAwait(false);
                LogSubscriptionCreated(queue.Name, sourceTopic);
            }
            catch (RpcException rpc) when (rpc.StatusCode == StatusCode.AlreadyExists)
            {
                // Idempotent — subscription already exists; skip.
                LogSubscriptionAlreadyExists(queue.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new TopologyDeploymentException(
                    topologyElement: queue.Name,
                    transportName: TransportName,
                    brokerError: ex.Message,
                    endpointAddress: null,
                    innerException: ex);
            }
        }

        foreach (ExchangeExchangeBinding binding in topology.ExchangeExchangeBindings)
        {
            LogExchangeExchangeBindingSkipped(binding.SourceExchangeName, binding.DestinationExchangeName);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _publisher = null;
        _subscriber = null;

        _clientLock.Dispose();

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ── Lazy connection helpers ───────────────────────────────────────────────

    private async Task EnsureClientsAsync(CancellationToken cancellationToken)
    {
        // Fast path: both clients already built.
        if (_publisher is not null && _subscriber is not null)
        {
            return;
        }

        await _clientLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock.
            if (_publisher is not null && _subscriber is not null)
            {
                return;
            }

            // SEC-02: secrets are never logged — only log non-secret identifiers.
            (_publisher, _subscriber) = BuildClients();
            string authModeStr = _options.AuthMode.ToString();
            LogClientCreated(_options.ProjectId, authModeStr);
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private (PublisherServiceApiClient Publisher, SubscriberServiceApiClient Subscriber) BuildClients()
    {
        if (_options.AuthMode == PubSubAuthMode.EmulatorInsecure)
        {
            // SEC-3: insecure channel credentials ONLY when EmulatorInsecure is explicitly chosen.
            var publisherBuilder = new PublisherServiceApiClientBuilder
            {
                Endpoint = _options.EmulatorEndpoint,
                ChannelCredentials = ChannelCredentials.Insecure,
            };

            var subscriberBuilder = new SubscriberServiceApiClientBuilder
            {
                Endpoint = _options.EmulatorEndpoint,
                ChannelCredentials = ChannelCredentials.Insecure,
            };

            return (publisherBuilder.Build(), subscriberBuilder.Build());
        }

        if (_options.AuthMode == PubSubAuthMode.ServiceAccountJson)
        {
            GoogleCredential credential;

            if (!string.IsNullOrEmpty(_options.ServiceAccountJsonPath))
            {
                using var stream = File.OpenRead(_options.ServiceAccountJsonPath);
                credential = GoogleCredential.FromStream(stream);
            }
            else
            {
                credential = GoogleCredential.FromJson(_options.ServiceAccountJson);
            }

            var pubCredential = credential.CreateScoped(PublisherServiceApiClient.DefaultScopes);
            var subCredential = credential.CreateScoped(SubscriberServiceApiClient.DefaultScopes);

            var publisherBuilder = new PublisherServiceApiClientBuilder
            {
                Credential = pubCredential,
            };

            var subscriberBuilder = new SubscriberServiceApiClientBuilder
            {
                Credential = subCredential,
            };

            return (publisherBuilder.Build(), subscriberBuilder.Build());
        }

        // ApplicationDefault: use Google ADC.
        return (PublisherServiceApiClient.Create(), SubscriberServiceApiClient.Create());
    }

    // ── Logging (source-gen partial methods) ──────────────────────────────────
    // SEC-02: never bind secrets. ProjectId is a non-secret GCP identifier.

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Google Cloud Pub/Sub clients created. ProjectId: {ProjectId}, AuthMode: {AuthMode}.")]
    private partial void LogClientCreated(string projectId, string authMode);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pub/Sub topic '{TopicName}' created successfully.")]
    private partial void LogTopicCreated(string topicName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pub/Sub topic '{TopicName}' already exists — skipping (idempotent declaration).")]
    private partial void LogTopicAlreadyExists(string topicName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pub/Sub subscription '{SubscriptionName}' created for topic '{TopicName}'.")]
    private partial void LogSubscriptionCreated(string subscriptionName, string topicName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pub/Sub subscription '{SubscriptionName}' already exists — skipping (idempotent declaration).")]
    private partial void LogSubscriptionAlreadyExists(string subscriptionName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pub/Sub topology deploy: binding '{ExchangeName}' -> '{QueueName}' mapped to topic→subscription.")]
    private partial void LogBindingMapped(string exchangeName, string queueName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pub/Sub topology deploy: exchange-to-exchange binding '{Source}' -> '{Destination}' skipped — Pub/Sub has no exchange-to-exchange concept.")]
    private partial void LogExchangeExchangeBindingSkipped(string source, string destination);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Pub/Sub message published to topic '{TopicName}'. MessageId: {MessageId}.")]
    private partial void LogMessageSent(string topicName, string messageId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pub/Sub message publish failed for topic '{TopicName}' — no MessageId returned.")]
    private partial void LogMessageSendFailed(string topicName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pub/Sub dead-letter topic '{DeadLetterTopic}' created for subscription '{SubscriptionName}'.")]
    private partial void LogDeadLetterTopicCreated(string deadLetterTopic, string subscriptionName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pub/Sub dead-letter topic '{DeadLetterTopic}' already exists — skipping (idempotent declaration).")]
    private partial void LogDeadLetterTopicAlreadyExists(string deadLetterTopic);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pub/Sub DeadLetterPolicy applied to subscription '{SubscriptionName}': dead-letter topic '{DeadLetterTopic}', max delivery attempts {MaxDeliveryAttempts}.")]
    private partial void LogDeadLetterPolicyApplied(string subscriptionName, string deadLetterTopic, int maxDeliveryAttempts);
}
