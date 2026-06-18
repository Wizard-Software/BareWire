using System.Collections.Concurrent;
using System.Globalization;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AWS.SQS.Internal;
using BareWire.Transport.AWS.SQS.Topology;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.AWS.SQS;

/// <summary>
/// Amazon SQS transport adapter. Implements producer (<see cref="SendBatchAsync"/>),
/// topology deployment (<see cref="DeployTopologyAsync"/>), and lifecycle management.
/// Consumer side (<see cref="ConsumeAsync"/>, <see cref="SettleAsync"/>) is implemented in
/// <c>SqsTransportAdapter.Consumer.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Uses a single, long-lived, thread-safe <see cref="IAmazonSQS"/> (<see cref="AmazonSQSClient"/>)
/// constructed lazily on first use under a <see cref="SemaphoreSlim"/> double-check lock.
/// </para>
/// <para>
/// <b>Producer body (OQ-1):</b> SQS <c>MessageBody</c> is a string field.
/// <c>OutboundMessage.Body</c> (<see cref="ReadOnlyMemory{T}"/>) is encoded via
/// <see cref="SqsHeaderMapper.EncodeBodyAsString"/>: textual content types (application/json,
/// text/*) are UTF-8 decoded; binary types (application/x-msgpack, application/octet-stream)
/// are Base64-encoded to avoid silent payload corruption.
/// </para>
/// <para>
/// <b>Capabilities note (R-1):</b> <see cref="TransportCapabilities.NativeDeduplication"/>
/// is declared because FIFO queues support native deduplication. BareWire-level
/// MessageGroupId / MessageDeduplicationId mapping is implemented in R4.2.
/// </para>
/// <para>
/// <b>Batch chunking (PERF-1):</b> SQS hard-limits <c>SendMessageBatch</c> to 10 entries.
/// <see cref="SendBatchAsync"/> uses index-based slicing with pre-allocated arrays (no
/// <c>Skip/Take/Select/.ToList()</c> chains) to avoid O(n²) allocation overhead.
/// </para>
/// </remarks>
internal sealed partial class SqsTransportAdapter : ITransportAdapter, IAsyncDisposable
{
    private const int MaxBatchSize = 10;

    /// <summary>
    /// Returns <see langword="true"/> when the given queue name or URL identifies a FIFO queue.
    /// SQS enforces that all FIFO queue names end with <c>.fifo</c>.
    /// </summary>
    private static bool IsFifoQueue(string queueNameOrUrl) =>
        queueNameOrUrl.EndsWith(".fifo", StringComparison.Ordinal);

    private readonly SqsTransportOptions _options;
    private readonly ILogger<SqsTransportAdapter> _logger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    // Kept as interface to allow NSubstitute mocking in unit tests (InternalsVisibleTo).
#pragma warning disable CA1859 // Use concrete types for improved performance
    private IAmazonSQS? _client;
#pragma warning restore CA1859
    private bool _disposed;

    // QueueUrl cache: queue name → QueueUrl. GetQueueUrlAsync is called at most once per queue.
    private readonly ConcurrentDictionary<string, string> _queueUrlCache =
        new(StringComparer.Ordinal);

    // Monotonic delivery tag counter (shared between producer and consumer partial classes).
    private ulong _deliveryTagCounter;

    internal SqsTransportAdapter(
        SqsTransportOptions options,
        ILogger<SqsTransportAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Internal test constructor that injects a pre-built <see cref="IAmazonSQS"/> client.
    /// Skips <c>EnsureClientAsync</c> by pre-populating <c>_client</c>.
    /// Available only to assemblies with <c>InternalsVisibleTo</c>.
    /// </summary>
    internal SqsTransportAdapter(
        SqsTransportOptions options,
        ILogger<SqsTransportAdapter> logger,
        IAmazonSQS client)
        : this(options, logger)
    {
        _client = client;
    }

    /// <inheritdoc />
    public string TransportName => "AWS.SQS";

    /// <inheritdoc />
    /// <remarks>
    /// <b>Capabilities note (R-1):</b>
    /// <list type="bullet">
    /// <item><term><see cref="TransportCapabilities.NativeDeduplication"/></term><description>FIFO queues support native dedup; BareWire MessageGroupId/MessageDeduplicationId mapping implemented in R4.2.</description></item>
    /// <item><term><see cref="TransportCapabilities.DlqNative"/></term><description>SQS RedrivePolicy routes exhausted messages to a DLQ automatically.</description></item>
    /// <item><term><see cref="TransportCapabilities.BatchReceive"/></term><description><c>ReceiveMessage</c> supports <c>MaxNumberOfMessages</c> up to 10.</description></item>
    /// </list>
    /// </remarks>
    public TransportCapabilities Capabilities =>
        TransportCapabilities.NativeDeduplication |
        TransportCapabilities.DlqNative |
        TransportCapabilities.BatchReceive;

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
            await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new BareWireTransportException(
                message: "Failed to establish Amazon SQS client connection before sending batch.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }

        var results = new SendResult[messages.Count];

        // Group messages by RoutingKey (= queue name).
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

        foreach ((string queueName, List<(int OriginalIndex, OutboundMessage Message)> group) in groups)
        {
            string queueUrl = await GetOrResolveQueueUrlAsync(queueName, cancellationToken)
                .ConfigureAwait(false);

            // Hoist FIFO detection outside the chunk loop — queueName is constant per group (GAP-1).
            bool isFifo = IsFifoQueue(queueName);

            // Chunk the group in batches of MaxBatchSize (10) using index-based slicing (PERF-1).
            int groupCount = group.Count;
            for (int offset = 0; offset < groupCount; offset += MaxBatchSize)
            {
                int chunkSize = Math.Min(MaxBatchSize, groupCount - offset);

                // PERF-2: Pre-allocate a List<T> of exact capacity and pass it directly to the
                // SDK constructor — avoids the double allocation of an array + [.. spread] copy.
                var entries = new List<SendMessageBatchRequestEntry>(chunkSize);

                // PERF-4: Compute each entry Id string exactly once during request construction
                // and store it in a parallel array so the matching loop can reuse it without a
                // second ToString call.
                var entryIds = new string[chunkSize];

                for (int j = 0; j < chunkSize; j++)
                {
                    (int originalIndex, OutboundMessage outbound) = group[offset + j];

                    // Id is the position within THIS batch chunk — used to match
                    // Successful/Failed responses back to original indices positionally.
                    string entryId = j.ToString(CultureInfo.InvariantCulture);
                    entryIds[j] = entryId;

                    // Resolve FIFO fields before building the entry (GAP-1: set in initializer).
                    string? groupId = null;
                    string? dedupId = null;

                    if (isFifo)
                    {
                        groupId = SqsFifoMapper.ResolveMessageGroupId(outbound.Headers);

                        // SEC: guard message contains queue name and header NAMES only — never values.
                        if (string.IsNullOrEmpty(groupId))
                        {
                            throw new BareWireTransportException(
                                message: $"FIFO queue '{queueName}' requires a MessageGroupId. " +
                                         $"Set the '{SqsHeaderMapper.MessageGroupIdHeader}' or " +
                                         $"'{SqsHeaderMapper.CorrelationIdHeader}' header before sending.",
                                transportName: TransportName,
                                endpointAddress: null);
                        }

                        dedupId = SqsFifoMapper.ResolveOrGenerateDeduplicationId(
                            outbound.Headers, groupId, outbound.Body.Span,
                            _options.EnableContentBasedDeduplication);
                    }

                    entries.Add(new SendMessageBatchRequestEntry
                    {
                        Id = entryId,
                        MessageBody = SqsHeaderMapper.EncodeBodyAsString(outbound.Body, outbound.ContentType),
                        MessageAttributes = SqsHeaderMapper.MapOutbound(outbound.Headers),
                        MessageGroupId = groupId,
                        MessageDeduplicationId = dedupId,
                    });
                }

                SendMessageBatchResponse response;
                try
                {
                    // PERF-2: entries is already a List<T> — pass it directly, no spread copy.
                    response = await _client!.SendMessageBatchAsync(
                        new SendMessageBatchRequest(queueUrl, entries),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new BareWireTransportException(
                        message: $"Failed to send message batch to SQS queue '{queueName}'.",
                        transportName: TransportName,
                        endpointAddress: null,
                        innerException: ex);
                }

                // PERF-1: Build O(n) dictionaries from the response lists ONCE before the
                // per-entry loop. Successful and Failed are unordered — index by Id for O(1)
                // TryGetValue per entry instead of O(n) FirstOrDefault scans inside the loop.
                var successById = new Dictionary<string, SendMessageBatchResultEntry>(
                    response.Successful.Count, StringComparer.Ordinal);
                foreach (SendMessageBatchResultEntry s in response.Successful)
                {
                    successById[s.Id] = s;
                }

                var failedById = new Dictionary<string, BatchResultErrorEntry>(
                    response.Failed.Count, StringComparer.Ordinal);
                foreach (BatchResultErrorEntry f in response.Failed)
                {
                    failedById[f.Id] = f;
                }

                for (int j = 0; j < chunkSize; j++)
                {
                    (int originalIndex, _) = group[offset + j];

                    // PERF-4: Reuse the Id string computed during request construction.
                    string entryId = entryIds[j];
                    bool confirmed = successById.ContainsKey(entryId);

                    // GAP-2: SendResult has only IsConfirmed + DeliveryTag; SQS MessageId is
                    // available in successById[entryId].MessageId but not surfaced here.
                    results[originalIndex] = new SendResult(
                        IsConfirmed: confirmed,
                        DeliveryTag: (ulong)(originalIndex + 1));

                    if (confirmed)
                    {
                        // Log the SQS MessageId for diagnostics without putting it in SendResult.
                        // PERF-1: O(1) lookup via successById dictionary.
                        LogMessageSent(queueName, successById[entryId].MessageId);
                    }
                    else
                    {
                        // PERF-1: O(1) lookup via failedById dictionary.
                        failedById.TryGetValue(entryId, out BatchResultErrorEntry? failEntry);
                        LogMessageSendFailed(queueName, failEntry?.Code ?? "Unknown", failEntry?.Message ?? string.Empty);
                    }
                }
            }
        }

        return results;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Creates SQS queues from <see cref="TopologyDeclaration.Queues"/> using
    /// <c>IAmazonSQS.CreateQueueAsync</c>. Exchanges and bindings are accepted (shared contract)
    /// but produce no admin operations — SQS has no exchange concept (mirror ASB D-6).
    /// Queue-already-exists responses are swallowed (idempotent declaration).
    /// </remarks>
    public async Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);

        foreach (QueueDeclaration queue in topology.Queues)
        {
            SqsQueueSpec spec = SqsTopologyArguments.Parse(queue);

            var attributes = new Dictionary<string, string>
            {
                ["VisibilityTimeout"] = ((int)spec.VisibilityTimeout.TotalSeconds)
                    .ToString(CultureInfo.InvariantCulture),
                ["ReceiveMessageWaitTimeSeconds"] = spec.WaitTimeSeconds
                    .ToString(CultureInfo.InvariantCulture),
            };

            if (spec.IsFifo)
            {
                attributes["FifoQueue"] = "true";

                if (spec.ContentBasedDeduplication)
                {
                    attributes["ContentBasedDeduplication"] = "true";
                }
            }

            if (spec.MaxReceiveCount > 0)
            {
                // RedrivePolicy requires a DLQ ARN — for R4.1 we set only the maxReceiveCount
                // without a DeadLetterTargetArn; the user must supply the full RedrivePolicy
                // JSON via queue Arguments if a DLQ ARN is known. We store count as a hint.
                // Full RedrivePolicy wiring is part of R4.4 (integration tests).
            }

            var request = new CreateQueueRequest
            {
                QueueName = queue.Name,
                Attributes = attributes,
            };

            try
            {
                CreateQueueResponse response = await _client!
                    .CreateQueueAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                // Cache the QueueUrl returned from creation.
                _queueUrlCache[queue.Name] = response.QueueUrl;
                LogQueueCreated(queue.Name, response.QueueUrl);
            }
            catch (Amazon.SQS.Model.QueueNameExistsException)
            {
                // Idempotent — queue already exists with different attributes; log and skip.
                LogQueueAlreadyExists(queue.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // QueueAlreadyExists (same attributes) in SQS returns the URL in a normal 200 response;
                // QueueNameExistsException is only thrown when attributes differ.
                // Check if the exception message indicates the queue already exists.
                if (ex.Message.Contains("QueueAlreadyExists", StringComparison.OrdinalIgnoreCase))
                {
                    LogQueueAlreadyExists(queue.Name);
                    continue;
                }

                throw new TopologyDeploymentException(
                    topologyElement: queue.Name,
                    transportName: TransportName,
                    brokerError: ex.Message,
                    endpointAddress: null,
                    innerException: ex);
            }
        }

        foreach (ExchangeDeclaration exchange in topology.Exchanges)
        {
            LogExchangeSkipped(exchange.Name);
        }

        foreach (ExchangeQueueBinding binding in topology.ExchangeQueueBindings)
        {
            LogBindingSkipped(binding.ExchangeName, binding.QueueName);
        }

        foreach (ExchangeExchangeBinding binding in topology.ExchangeExchangeBindings)
        {
            LogBindingSkipped(binding.SourceExchangeName, binding.DestinationExchangeName);
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

        if (_client is not null)
        {
            _client.Dispose();
            _client = null;
        }

        _clientLock.Dispose();
    }

    // ── Lazy connection helpers ───────────────────────────────────────────────

    private async Task EnsureClientAsync(CancellationToken cancellationToken)
    {
        // Fast path: client already built.
        if (_client is not null)
        {
            return;
        }

        await _clientLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock.
            if (_client is not null)
            {
                return;
            }

            // SEC-02: secrets are never logged.
            _client = BuildClient();

            // Log only non-secret identifiers.
            string regionInfo = string.IsNullOrEmpty(_options.RegionEndpoint)
                ? "(from environment)"
                : _options.RegionEndpoint;

            string endpointInfo = string.IsNullOrEmpty(_options.ServiceUrl)
                ? $"AWS SQS {regionInfo}"
                : _options.ServiceUrl;

            LogClientCreated(endpointInfo);
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private AmazonSQSClient BuildClient()
    {
        var config = new AmazonSQSConfig();

        if (!string.IsNullOrEmpty(_options.RegionEndpoint))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(_options.RegionEndpoint);
        }

        if (!string.IsNullOrEmpty(_options.ServiceUrl))
        {
            config.ServiceURL = _options.ServiceUrl;
        }

        return _options.AuthMode switch
        {
            SqsAuthMode.Explicit =>
                new AmazonSQSClient(
                    new BasicAWSCredentials(_options.AccessKeyId, _options.SecretAccessKey),
                    config),
            _ =>
                // DefaultChain: AWS SDK resolves credentials from environment, instance metadata, etc.
                new AmazonSQSClient(config),
        };
    }

    private async Task<string> GetOrResolveQueueUrlAsync(string queueName, CancellationToken cancellationToken)
    {
        if (_queueUrlCache.TryGetValue(queueName, out string? cached))
        {
            return cached;
        }

        try
        {
            GetQueueUrlResponse response = await _client!
                .GetQueueUrlAsync(queueName, cancellationToken)
                .ConfigureAwait(false);

            string url = response.QueueUrl;
            _queueUrlCache[queueName] = url;
            return url;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BareWireTransportException(
                message: $"Failed to resolve SQS queue URL for queue '{queueName}'.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }
    }

    // ── Logging (source-gen partial methods) ──────────────────────────────────
    // SEC-02: never bind secrets (AccessKeyId is an identifier, safe to log; SecretAccessKey must never appear).

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Amazon SQS client created. Endpoint: {Endpoint}.")]
    private partial void LogClientCreated(string endpoint);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "SQS queue '{QueueName}' created successfully. URL: {QueueUrl}.")]
    private partial void LogQueueCreated(string queueName, string queueUrl);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "SQS queue '{QueueName}' already exists — skipping (idempotent declaration).")]
    private partial void LogQueueAlreadyExists(string queueName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "SQS topology deploy: exchange '{ExchangeName}' skipped — SQS has no exchange concept.")]
    private partial void LogExchangeSkipped(string exchangeName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "SQS topology deploy: binding '{SourceName}' -> '{DestinationName}' skipped — SQS has no binding concept.")]
    private partial void LogBindingSkipped(string sourceName, string destinationName);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "SQS message sent to queue '{QueueName}'. SQS MessageId: {MessageId}.")]
    private partial void LogMessageSent(string queueName, string messageId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SQS message send failed for queue '{QueueName}'. Code: {Code}. Detail: {Detail}.")]
    private partial void LogMessageSendFailed(string queueName, string code, string detail);
}
