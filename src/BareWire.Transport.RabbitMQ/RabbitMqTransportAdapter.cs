using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ.Configuration;
using BareWire.Transport.RabbitMQ.Internal;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using BwExchangeType = BareWire.Abstractions.ExchangeType;

namespace BareWire.Transport.RabbitMQ;

internal sealed partial class RabbitMqTransportAdapter
    : ITransportAdapter, IConsumerChannelManager, IDurableParkSettlement, ITransportHealthSource, IAsyncDisposable
{
    private readonly RabbitMqTransportOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Action<RabbitMqConsumer, TimeSpan> _onRecoveryOverdue;
    private readonly Action<RabbitMqConsumer, TimeSpan> _onRecoveredAfterOverdue;
    private readonly ILogger<RabbitMqTransportAdapter> _logger;
    private readonly RabbitMqHeaderMapper _headerMapper;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    // Captured once: the source is cancelled before it is disposed, and Token throws on a disposed source.
    private readonly CancellationToken _disposeToken;

    // Maps consumerChannelId (the BW-ConsumerChannelId header stamped on every delivery) → the IChannel that
    // delivered it, so SettleAsync can ACK/NACK messages on the correct channel without threading channel
    // references through InboundMessage.
    private readonly ConcurrentDictionary<string, IChannel> _activeConsumerChannels =
        new(StringComparer.Ordinal);

    // Maps consumerChannelId → the consumer, so SettleAsync(Requeue) can cancel the AMQP consumer before
    // the nack when the caller is shutting down. Looked up only by the BW-ConsumerChannelId header; entries
    // are removed together with the channel: on release (ReleaseConsumerChannelAsync), on adapter DisposeAsync,
    // when consumer setup fails (basic.qos or basic.consume), and when an iteration ends without having handed
    // out any delivery.
    private readonly ConcurrentDictionary<string, RabbitMqConsumer> _activeConsumers =
        new(StringComparer.Ordinal);

    private const string ChannelIdHeaderName = "BW-ConsumerChannelId";
    private const int MaxLoggedChannelIdLength = 64;

    // Budget of one shutdown step (closing a consumer channel, closing the connection, disposing either).
    private static readonly TimeSpan ShutdownStepTimeout =
        TimeSpan.FromMilliseconds(RabbitMqConsumer.ShutdownStepTimeoutMilliseconds);

    // The connection-close token outlives the limit handed to the client by one second, so the client's own
    // limit is the one that normally ends the close and the token is only the backstop.
    private static readonly TimeSpan ConnectionCloseTokenTimeout = ShutdownStepTimeout + TimeSpan.FromSeconds(1);

    private long _deliveryTagCounter;
    private IConnection? _connection;
    private int _disposed;

    // Creates the broker connection. A seam for tests, which replace it to block or fail connection setup.
    internal Func<ConnectionFactory, CancellationToken, Task<IConnection>> CreateConnection { get; set; } =
        static (factory, cancellationToken) => factory.CreateConnectionAsync(cancellationToken);

    // Mapping-epoch memoization — keyed on topology reference (O(1) ReferenceEquals per batch).
    // Recomputed only when the configured topology reference is reassigned (re-map scenario).
    // Pre-boxing the epoch as object eliminates per-message boxing allocation in the publish loop.
    // These fields are written in SendBatchAsync without synchronization; correctness relies on the
    // single-writer publish-loop contract documented on SendBatchAsync (no concurrent send batches).
    private TopologyDeclaration? _cachedEpochTopology;
    private long? _cachedEpoch;
    private object? _cachedEpochBoxed;

    /// <summary>
    /// AMQP header carrying the topology-derived mapping epoch (FNV-1a of the sorted set of
    /// consistent-hash bound queue names). A best-effort, topology-derived change-detection
    /// signal that lets a consumer detect a re-map of the consistent-hash key space; it is
    /// NOT a security-authenticated or integrity field. Value type: <see cref="long"/>.
    /// </summary>
    internal const string MappingEpochHeaderName = "BW-MappingEpoch";

    public RabbitMqTransportAdapter(
        RabbitMqTransportOptions options,
        ILogger<RabbitMqTransportAdapter> logger,
        RabbitMqHeaderMapper? headerMapper = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        _options = options;
        _logger = logger;
        _headerMapper = headerMapper ?? new RabbitMqHeaderMapper();
        _disposeToken = _disposeCts.Token;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onRecoveryOverdue = (consumer, waited) =>
            LogConsumerRecoveryOverdue(consumer.EndpointName, (long)waited.TotalSeconds);
        _onRecoveredAfterOverdue = (consumer, waited) =>
            LogConsumerRecoveredAfterOverdue(consumer.EndpointName, (long)waited.TotalSeconds);

        LogPublishRoutingDivergences();
    }

    // DEFAULT-ON, zero-extra-configuration diagnostic: surface every divergent per-type publish-routing
    // overwrite detected at config time as a warning. Last-call-wins already applied — these warnings
    // only make the divergence LOUD so an operator can reconcile the conflicting registration calls.
    private void LogPublishRoutingDivergences()
    {
        if (_options.PublishRoutingDivergences is not { Count: > 0 } divergences)
        {
            return;
        }

        foreach (Configuration.PublishRoutingDivergence divergence in divergences)
        {
            LogPublishRoutingDivergence(
                divergence.MessageType.FullName ?? divergence.MessageType.Name,
                divergence.Dimension.ToString(),
                divergence.PreviousValue,
                divergence.NewValue);
        }
    }

    public string TransportName => "RabbitMQ";

    public TransportCapabilities Capabilities =>
        TransportCapabilities.PublisherConfirms |
        TransportCapabilities.DlqNative |
        TransportCapabilities.FlowControl;

    /// <remarks>
    /// Assumes a single-writer caller (the bus publisher loop): batches are not sent concurrently.
    /// The mapping-epoch memoization fields are read/written here without synchronization and rely
    /// on that contract.
    /// </remarks>
    public async Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new BareWireTransportException(
                message: "Failed to establish connection before sending batch.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }

        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);

        IChannel channel = await _connection!
            .CreateChannelAsync(channelOptions, cancellationToken)
            .ConfigureAwait(false);

        var results = new SendResult[messages.Count];

        // Memoize the mapping epoch keyed on the topology reference.
        // ReferenceEquals is O(1) — recompute only when _options.Topology is reassigned.
        // The pre-boxed object reference is reused for every message in the batch → zero
        // per-message boxing allocation (the box is created once per topology change).
        if (!ReferenceEquals(_cachedEpochTopology, _options.Topology))
        {
            _cachedEpochTopology = _options.Topology;
            _cachedEpoch = MappingEpochCalculator.Compute(_options.Topology);
            _cachedEpochBoxed = _cachedEpoch is { } e ? (object)e : null;
        }

        // Opt-in guaranteed-routing: publish mandatory so the broker returns (rather than silently
        // drops) an unroutable message. Read once per batch (PERF) — the publish loop is single-writer.
        bool mandatory = _options.GuaranteedRouting;

        try
        {
            for (int i = 0; i < messages.Count; i++)
            {
                OutboundMessage outbound = messages[i];

                bool hasExchangeHeader = outbound.Headers.TryGetValue("BW-Exchange", out string? headerExchange);
                string exchange = hasExchangeHeader
                    ? headerExchange!
                    : _options.DefaultExchange;

                // Fail-fast: throw only when the caller did NOT supply a BW-Exchange header AND no
                // DefaultExchange is configured. If the header is present (even as ""), honour it —
                // BareWireSendEndpoint uses BW-Exchange="" for the queue: scheme (AMQP default exchange).
                if (!hasExchangeHeader && string.IsNullOrEmpty(_options.DefaultExchange))
                {
                    throw new BareWireConfigurationException(
                        optionName: "Exchange",
                        optionValue: exchange,
                        expectedValue: "No exchange resolved for the outbound message. " +
                                       "Configure a DefaultExchange, call MapExchange<T>(), " +
                                       "or pass the BW-Exchange header on each publish call per ADR-002.");
                }

                ulong deliveryTag = (ulong)Interlocked.Increment(ref _deliveryTagCounter);

                (BasicProperties props, Dictionary<string, object?> amqpHeaders) =
                    _headerMapper.MapOutbound(outbound.Headers);

                // Stamp the mapping epoch directly into the AMQP header dictionary (not into
                // outbound.Headers) because RabbitMqHeaderMapper.MapOutbound deliberately drops
                // all "BW-" prefixed canonical headers. The pre-boxed reference is reused for
                // every message in the batch — zero per-message allocation when topology is stable.
                if (_cachedEpochBoxed is not null)
                {
                    amqpHeaders[MappingEpochHeaderName] = _cachedEpochBoxed;
                }

                // ContentType is always set from the OutboundMessage's dedicated field
                // (takes precedence over any "content-type" header in the dictionary).
                if (!string.IsNullOrEmpty(outbound.ContentType))
                {
                    props.ContentType = outbound.ContentType;
                }

                if (amqpHeaders.Count > 0)
                {
                    props.Headers = amqpHeaders;
                }

                try
                {
                    await channel.BasicPublishAsync<BasicProperties>(
                        exchange: exchange,
                        routingKey: outbound.RoutingKey,
                        mandatory: mandatory,
                        basicProperties: props,
                        body: outbound.Body,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    results[i] = new SendResult(IsConfirmed: true, DeliveryTag: deliveryTag);
                }
                catch (PublishException ex) when (ex.IsReturn)
                {
                    // Guaranteed-routing mode only (mandatory:true): the broker accepted the publish
                    // but could not route it to any queue and returned it (surfaced synchronously as
                    // PublishException.IsReturn because the channel has publisher-confirm tracking on).
                    // Report it as not confirmed so the caller (e.g. the outbox) retries instead of
                    // treating an unroutable message as delivered. Continue the batch (per-message result).
                    // SEC: log only the exchange + routing key (topology structure), never body/headers.
                    LogPublishMessageReturned(exchange, outbound.RoutingKey);
                    results[i] = new SendResult(IsConfirmed: false, DeliveryTag: deliveryTag);
                }
                catch (PublishException)
                {
                    // Broker sent a Nack — message was not durably stored. Continue the batch.
                    results[i] = new SendResult(IsConfirmed: false, DeliveryTag: deliveryTag);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BareWireTransportException(
                message: "Connection-level failure during batch send.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }
        finally
        {
            try
            {
                await channel.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogPublishChannelCloseError(ex);
            }

            await channel.DisposeAsync().ConfigureAwait(false);
        }

        return results;
    }

    public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpointName);
        ArgumentNullException.ThrowIfNull(flowControl);
        ValidateFlowControl(flowControl);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new BareWireTransportException(
                message: $"Failed to establish connection before consuming from '{endpointName}'.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }

        // Capture the connection that owns this channel: the field is replaced when the adapter reconnects, and
        // the consumer's recovery probe must describe this connection, not whichever is current later.
        IConnection owner = _connection!;

        // consumerDispatchConcurrency: 1 serialises deliveries and shutdown events of the channel, which the
        // consumer relies on (single writer for its delivery-tag tracking).
        IChannel consumerChannel = await owner
            .CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: false,
                    publisherConfirmationTrackingEnabled: false,
                    consumerDispatchConcurrency: 1),
                cancellationToken)
            .ConfigureAwait(false);

        // Each ConsumeAsync invocation gets a unique key so multiple consumers on the same
        // endpoint (round-robin) do not overwrite each other's channel in the dictionary.
        // Register the channel immediately so every failure below releases it through one path, and so adapter
        // DisposeAsync closes it if the adapter is torn down mid-setup. It also lets SettleAsync resolve it by
        // consumer channel ID.
        string consumerChannelId = Guid.NewGuid().ToString("N");
        _activeConsumerChannels[consumerChannelId] = consumerChannel;

        // Pairs with the CompareExchange in DisposeAsync: either its sweep sees the registration above, or the
        // re-check below sees the disposed flag (store -> load ordering needs a full fence on both sides).
        Interlocked.MemoryBarrier();

        RabbitMqConsumer? consumer = null;
        Channel<InboundMessage>? inboundChannel = null;
        string assignedTag;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            // ValidateFlowControl guarantees MaxInFlightMessages > 0 at int level, so the narrowing cast is safe.
            ushort prefetchCount = (ushort)Math.Min(flowControl.MaxInFlightMessages, ushort.MaxValue);

            await consumerChannel.BasicQosAsync(
                prefetchSize: 0,
                prefetchCount: prefetchCount,
                global: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Invariant: the broker never has more than prefetchCount unacknowledged deliveries for this consumer
            // (basic.qos with global: false applies per consumer, and there is one consumer per channel), so a
            // buffer of at least that size cannot be full while the writer is open. The buffer stays bounded
            // (at most ushort.MaxValue entries).
            int bufferCapacity = Math.Max(flowControl.InternalQueueCapacity, prefetchCount);
            inboundChannel = Channel.CreateBounded<InboundMessage>(
                new BoundedChannelOptions(bufferCapacity)
                {
                    FullMode = flowControl.FullMode,
                    SingleWriter = true,
                    SingleReader = false,
                });

            consumer = new RabbitMqConsumer(consumerChannel, inboundChannel, _headerMapper, consumerChannelId)
            {
                EndpointName = endpointName,
                AutomaticRecoveryEnabled = _options.AutomaticRecoveryEnabled,
                IsConnectionOpen = () => owner.IsOpen,
                TimeProvider = _timeProvider,
                RecoveryHealthTimeout = _options.EffectiveRecoveryHealthTimeout,
                RecoveryOverdue = _onRecoveryOverdue,
                RecoveredAfterOverdue = _onRecoveredAfterOverdue,
            };
            _activeConsumers[consumerChannelId] = consumer;

            assignedTag = await consumerChannel.BasicConsumeAsync(
                queue: endpointName,
                autoAck: false,
                consumerTag: string.Empty,
                noLocal: false,
                exclusive: false,
                arguments: null,
                consumer: consumer,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Setup failed and nothing was handed to the caller. Stop buffering, return the pooled buffers of any
            // deliveries that arrived after consume-ok without a nack (closing the channel makes the broker
            // return them), release the channel and rethrow the original failure.
            consumer?.CompleteWriter();
            if (inboundChannel is not null)
            {
                while (inboundChannel.Reader.TryRead(out InboundMessage? buffered))
                {
                    buffered.Dispose();
                }
            }

            await ReleaseConsumerChannelSafeAsync(endpointName, consumerChannelId).ConfigureAwait(false);
            throw;
        }

        // Must not throw: this region runs after the setup catch and before the iterator's try/finally, so an
        // exception here would leak the channel. It is a property assignment and a token registration whose
        // callback (RequestStop) only flips a flag; it stays outside the setup try because the `await using`
        // registration must live for the whole loop.
        consumer.AssignedTag = assignedTag;

        // Flag the consumer as stopping the moment the caller's token fires, before the runner settles the
        // in-flight message with Requeue (see SettleAsync).
        await using CancellationTokenRegistration stopRegistration = cancellationToken.Register(
            static state => ((RabbitMqConsumer)state!).RequestStop(), consumer);

        bool deliveredAny = false;
        try
        {
            await foreach (InboundMessage message in consumer.ReadUntilCancelledAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                deliveredAny = true;
                yield return message;
            }
        }
        finally
        {
            // Shutdown order matters. The channel stays open (the caller still settles in-flight messages via
            // SettleAsync; it is closed by ReleaseConsumerChannelAsync or DisposeAsync), but:
            //   1. Cancel the AMQP consumer and wait for cancel-ok: from then on the broker cannot hand any
            //      message back to this consumer. Bounded to a few seconds so a stuck broker cannot consume the
            //      host's shutdown budget.
            //   2. Complete the writer.
            //   3. Hand back whatever is still buffered. Those deliveries were never given to the caller, so the
            //      caller will never settle them; without this they stay unacknowledged until the channel closes
            //      and their pooled buffers never return to ArrayPool. The drain neither releases FlowController
            //      credits (these messages never received any) nor uses multiple: true (that would also settle
            //      in-flight deliveries owned by the runner).
            //   4. If the cancel did not complete (channel not open, failure or timeout) the consumer may still
            //      be fed by the broker; close the channel so the broker returns every unsettled delivery once.
            //   4a. If no delivery was ever handed to the caller, nothing can be settled on this channel and the
            //      caller never learns its id (it travels only on deliveries), so the adapter releases it here
            //      instead of step 4.
            bool cancelled = await consumer.EnsureCancelledAsync().ConfigureAwait(false);
            if (!cancelled && consumer.CancelFailure is { } cancelFailure)
            {
                LogConsumeChannelCloseError(endpointName, cancelFailure);
            }

            consumer.CompleteWriter();
            consumer.StopRecoveryTracking();

            using (var drainTimeout = new CancellationTokenSource(RabbitMqConsumer.ShutdownStepTimeoutMilliseconds))
            {
                RabbitMqConsumer.DrainResult drain =
                    await consumer.DrainAndRequeueAsync(drainTimeout.Token).ConfigureAwait(false);
                if (drain.FirstFailure is not null)
                {
                    LogShutdownDrainNackError(
                        endpointName, drain.FirstFailedTag, drain.FirstFailedMessageId ?? string.Empty,
                        drain.SkippedNacks, drain.FirstFailure);
                }
            }

            if (!deliveredAny)
            {
                await ReleaseConsumerChannelSafeAsync(endpointName, consumerChannelId).ConfigureAwait(false);
            }
            else if (!cancelled && consumerChannel.IsOpen)
            {
                try
                {
                    using var closeTimeout = new CancellationTokenSource(
                        RabbitMqConsumer.ShutdownStepTimeoutMilliseconds);
                    await consumerChannel.CloseAsync(closeTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogConsumeChannelCloseError(endpointName, ex);
                }
            }
        }
    }

    private static void ValidateFlowControl(FlowControlOptions flowControl)
    {
        // Checked on the int values, before any narrowing to ushort (a negative value would wrap to a huge prefetch,
        // and 0 means an unlimited AMQP prefetch).
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flowControl.MaxInFlightMessages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flowControl.InternalQueueCapacity);

        if (flowControl.FullMode != BoundedChannelFullMode.Wait)
        {
            // Defence-in-depth guarding the "buffer >= prefetch" invariant: if that invariant ever changed,
            // the Drop* modes would silently lose deliveries without settling them with the broker.
            throw new ArgumentOutOfRangeException(
                nameof(flowControl),
                flowControl.FullMode,
                "RabbitMQ consumers support only BoundedChannelFullMode.Wait.");
        }
    }

    public async Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        IChannel? channel = ResolveChannelForMessage(message);

        if (channel is null)
        {
            // Formatting happens only on this failure path; the success path allocates nothing extra.
            string reason;
            string? loggedChannelId;
            if (message.Headers.TryGetValue(ChannelIdHeaderName, out string? headerValue) &&
                !string.IsNullOrEmpty(headerValue))
            {
                loggedChannelId = TruncateChannelId(headerValue);
                reason = $"the consumer channel '{loggedChannelId}' is no longer active.";
            }
            else
            {
                loggedChannelId = null;
                reason = $"the message carries no {ChannelIdHeaderName} header.";
            }

            LogSettlementChannelNotFound(message.MessageId, action, message.DeliveryTag, loggedChannelId);

            throw new BareWireTransportException(
                message: $"Delivery tag {message.DeliveryTag} cannot be settled: {reason}",
                transportName: TransportName,
                endpointAddress: null);
        }

        switch (action)
        {
            case SettlementAction.Ack:
                await channel.BasicAckAsync(
                    deliveryTag: message.DeliveryTag,
                    multiple: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            case SettlementAction.Nack:
                await channel.BasicNackAsync(
                    deliveryTag: message.DeliveryTag,
                    multiple: false,
                    requeue: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            case SettlementAction.Reject:
                await channel.BasicRejectAsync(
                    deliveryTag: message.DeliveryTag,
                    requeue: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            case SettlementAction.Requeue:
                // While the caller is shutting down, cancel the AMQP consumer first (single-flight) so the
                // broker cannot redeliver this very message to the consumer that is being stopped, which
                // would count it as delivered twice. A failed cancel is logged once and the nack still goes out.
                if (message.Headers.TryGetValue(ChannelIdHeaderName, out string? requeueChannelId) &&
                    _activeConsumers.TryGetValue(requeueChannelId, out RabbitMqConsumer? stoppingConsumer) &&
                    stoppingConsumer.IsStopRequested)
                {
                    bool cancelled = await stoppingConsumer.EnsureCancelledAsync().ConfigureAwait(false);
                    if (!cancelled && stoppingConsumer.TryClaimCancelFailureReport())
                    {
                        LogRequeueCancelFailed(
                            message.MessageId,
                            stoppingConsumer.CancelFailure);
                    }
                }

                await channel.BasicNackAsync(
                    deliveryTag: message.DeliveryTag,
                    multiple: false,
                    requeue: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            case SettlementAction.Defer:
                if (!_options.DeferEnabled)
                {
                    throw new NotSupportedException(
                        "SettlementAction.Defer requires DeferEnabled = true in RabbitMqTransportOptions. " +
                        "Enable it and deploy the corresponding DLQ+TTL topology before using Defer.");
                }

                // Nack without requeue — relies on the DLQ+TTL topology for deferred re-delivery.
                await channel.BasicNackAsync(
                    deliveryTag: message.DeliveryTag,
                    multiple: false,
                    requeue: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(action), action, $"Unknown {nameof(SettlementAction)}: {action}.");
        }
    }

    public async Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        IChannel channel = await _connection!.CreateChannelAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            foreach (ExchangeDeclaration exchange in topology.Exchanges)
            {
                try
                {
                    await channel.ExchangeDeclareAsync(
                        exchange: exchange.Name,
                        type: ToRabbitMqExchangeType(exchange.Type),
                        durable: exchange.Durable,
                        autoDelete: exchange.AutoDelete,
                        arguments: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationInterruptedException ex)
                {
                    throw new TopologyDeploymentException(
                        topologyElement: exchange.Name,
                        transportName: TransportName,
                        brokerError: ex.ShutdownReason?.ReplyText,
                        endpointAddress: null,
                        innerException: ex);
                }
            }

            foreach (QueueDeclaration queue in topology.Queues)
            {
                try
                {
                    await channel.QueueDeclareAsync(
                        queue: queue.Name,
                        durable: queue.Durable,
                        exclusive: queue.Exclusive,
                        autoDelete: queue.AutoDelete,
                        arguments: queue.Arguments?.ToDictionary(
                            kvp => kvp.Key, kvp => (object?)kvp.Value),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationInterruptedException ex)
                {
                    throw new TopologyDeploymentException(
                        topologyElement: queue.Name,
                        transportName: TransportName,
                        brokerError: ex.ShutdownReason?.ReplyText,
                        endpointAddress: null,
                        innerException: ex);
                }
            }

            foreach (ExchangeQueueBinding binding in topology.ExchangeQueueBindings)
            {
                try
                {
                    await channel.QueueBindAsync(
                        queue: binding.QueueName,
                        exchange: binding.ExchangeName,
                        routingKey: binding.RoutingKey,
                        arguments: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationInterruptedException ex)
                {
                    throw new TopologyDeploymentException(
                        topologyElement: $"{binding.ExchangeName} -> {binding.QueueName} [{binding.RoutingKey}]",
                        transportName: TransportName,
                        brokerError: ex.ShutdownReason?.ReplyText,
                        endpointAddress: null,
                        innerException: ex);
                }
            }

            foreach (ExchangeExchangeBinding binding in topology.ExchangeExchangeBindings)
            {
                try
                {
                    await channel.ExchangeBindAsync(
                        destination: binding.DestinationExchangeName,
                        source: binding.SourceExchangeName,
                        routingKey: binding.RoutingKey,
                        arguments: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationInterruptedException ex)
                {
                    throw new TopologyDeploymentException(
                        topologyElement: $"{binding.SourceExchangeName} -> {binding.DestinationExchangeName} [{binding.RoutingKey}]",
                        transportName: TransportName,
                        brokerError: ex.ShutdownReason?.ReplyText,
                        endpointAddress: null,
                        innerException: ex);
                }
            }
        }
        finally
        {
            await CloseAndDisposeTopologyChannelAsync(channel).ConfigureAwait(false);
        }
    }

    // Never throws: it runs in the finally of DeployTopologyAsync, so a close or dispose failure must neither
    // replace the topology exception (or the caller's cancellation) nor turn a successful deployment into a
    // failure. The close gets its own ShutdownStepTimeout budget on the adapter's clock, independent of the
    // caller's token, so a cancelled deployment still closes the channel gracefully; it is linked only to the
    // captured _disposeToken, so disposing the adapter ends it at once (and skips it when already disposed).
    // The channel is closed only while it is open: the broker may already have closed it after a failed
    // declaration. The dispose is bounded the same way as the close.
    private async Task CloseAndDisposeTopologyChannelAsync(IChannel channel)
    {
        try
        {
            if (channel.IsOpen && !_disposeToken.IsCancellationRequested)
            {
                using var budget = new CancellationTokenSource(ShutdownStepTimeout, _timeProvider);
                using var closeToken = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, _disposeToken);
                try
                {
                    await WaitBoundedAsync(channel.CloseAsync(closeToken.Token), closeToken.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                    when (_disposeToken.IsCancellationRequested && !budget.IsCancellationRequested)
                {
                    LogTopologyChannelCloseAbandoned(ex);
                }
                catch (Exception ex)
                {
                    LogTopologyChannelCloseError(ex);
                }
            }
        }
        catch (Exception ex)
        {
            LogTopologyChannelCloseError(ex);
        }

        try
        {
            await DisposeBoundedAsync(channel.DisposeAsync()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogTopologyChannelCloseError(ex);
        }
    }

    private IChannel? ResolveChannelForMessage(InboundMessage message)
    {
        // A delivery tag is valid only on the channel that issued it, so settlement must use exactly that
        // channel. There is deliberately no heuristic fallback: settling on another channel would ack someone
        // else's message or close the channel with PRECONDITION_FAILED.
        if (message.Headers.TryGetValue(ChannelIdHeaderName, out string? channelId) &&
            !string.IsNullOrEmpty(channelId) &&
            _activeConsumerChannels.TryGetValue(channelId, out IChannel? channel))
        {
            return channel;
        }

        return null;
    }

    private static string TruncateChannelId(string channelId) =>
        channelId.Length <= MaxLoggedChannelIdLength ? channelId : channelId[..MaxLoggedChannelIdLength];

    /// <inheritdoc />
    public Task ReleaseConsumerChannelAsync(string channelId, CancellationToken cancellationToken = default) =>
        ReleaseConsumerChannelCoreAsync(channelId, endpointName: null, cancellationToken);

    // Never throws: close and dispose failures are logged, so a caller's finally never sees them and the
    // original failure is not masked. The close is bounded by ShutdownStepTimeout on the adapter's clock whatever
    // token the caller passes (the receive endpoint runner passes CancellationToken.None while it is stopping),
    // and a caller's own cancellation still ends it at once.
    private async Task ReleaseConsumerChannelCoreAsync(
        string channelId, string? endpointName, CancellationToken cancellationToken)
    {
        // Intentional order: the consumer entry goes first, so nothing can resolve a consumer whose channel is
        // already on its way out; the channel entry decides who owns the close.
        _activeConsumers.TryRemove(channelId, out RabbitMqConsumer? consumer);
        consumer?.StopRecoveryTracking();

        // Whoever removes the entry (this method, a failed setup, the iterator's finally, or DisposeAsync) is the
        // only one to release it. Already released or never registered: no-op (idempotent).
        if (!_activeConsumerChannels.TryRemove(channelId, out IChannel? channel))
        {
            return;
        }

        using var budget = new CancellationTokenSource(ShutdownStepTimeout, _timeProvider);
        using var closeToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        await CloseAndDisposeConsumerChannelAsync(
                channel, endpointName ?? consumer?.EndpointName ?? string.Empty, channelId, closeToken.Token)
            .ConfigureAwait(false);
    }

    // Never throws: every caller has already removed the channel from the registry, so a failure here must not
    // stop the caller from releasing anything else. RabbitMQ.Client does not honour the token of CloseAsync while
    // the channel is still open (it waits for the broker up to its ContinuationTimeout), so the token also bounds
    // the wait itself; IChannel.DisposeAsync() takes no token and is bounded by ShutdownStepTimeout the same way.
    // A close or dispose abandoned this way keeps running in the client; its outcome is observed and dropped.
    private async Task CloseAndDisposeConsumerChannelAsync(
        IChannel channel, string endpointName, string channelId, CancellationToken cancellationToken)
    {
        try
        {
            if (channel.IsOpen)
            {
                await WaitBoundedAsync(channel.CloseAsync(cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogConsumerChannelReleaseError(endpointName, TruncateChannelId(channelId), ex);
        }

        try
        {
            await DisposeBoundedAsync(channel.DisposeAsync()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogConsumerChannelReleaseError(endpointName, TruncateChannelId(channelId), ex);
        }
    }

    // Stops waiting for work that ignores its token when the token is cancelled. The abandoned task is observed
    // so a later fault does not surface as an unobserved task exception.
    private static async Task WaitBoundedAsync(Task work, CancellationToken cancellationToken)
    {
        try
        {
            await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!work.IsCompleted)
        {
            ObserveAbandoned(work);
            throw;
        }
    }

    // Bounds a dispose, which takes no token, by ShutdownStepTimeout on the adapter's clock.
    private async Task DisposeBoundedAsync(ValueTask dispose)
    {
        Task work = dispose.AsTask();
        try
        {
            await work.WaitAsync(ShutdownStepTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!work.IsCompleted)
        {
            ObserveAbandoned(work);
            throw;
        }
    }

    private static void ObserveAbandoned(Task work) =>
        _ = work.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    // Used on setup failure and in the iterator's finally, where the original exception must surface.
    private async Task ReleaseConsumerChannelSafeAsync(string endpointName, string consumerChannelId)
    {
        // Independent of the caller's token: setup may have failed precisely because it was cancelled.
        using var timeout = new CancellationTokenSource(RabbitMqConsumer.ShutdownStepTimeoutMilliseconds);
        await ReleaseConsumerChannelCoreAsync(consumerChannelId, endpointName, timeout.Token).ConfigureAwait(false);
    }

    private static string ToRabbitMqExchangeType(BwExchangeType exchangeType) =>
        exchangeType switch
        {
            BwExchangeType.Direct         => "direct",
            BwExchangeType.Fanout         => "fanout",
            BwExchangeType.Topic          => "topic",
            BwExchangeType.Headers        => "headers",
            BwExchangeType.ConsistentHash => "x-consistent-hash",
            _ => throw new ArgumentOutOfRangeException(nameof(exchangeType), exchangeType, "Unknown exchange type."),
        };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        await _disposeCts.CancelAsync().ConfigureAwait(false);

        // Close consumer channels that were intentionally kept alive for post-iteration settlement. One TryRemove
        // pass, enumerating the dictionary directly. An entry removed here is released only here; a setup that
        // registers after this pass sees the disposed flag and releases its own channel, so no outer retry loop
        // is needed. The helper never throws, so one failing channel cannot skip the remaining channels, the
        // connection or the CTS.
        foreach (KeyValuePair<string, IChannel> entry in _activeConsumerChannels)
        {
            if (!_activeConsumerChannels.TryRemove(entry.Key, out IChannel? channel))
            {
                continue;
            }

            _activeConsumers.TryRemove(entry.Key, out RabbitMqConsumer? consumer);
            consumer?.StopRecoveryTracking();
            using var closeTimeout = new CancellationTokenSource(RabbitMqConsumer.ShutdownStepTimeoutMilliseconds);
            await CloseAndDisposeConsumerChannelAsync(
                    channel, consumer?.EndpointName ?? string.Empty, entry.Key, closeTimeout.Token)
                .ConfigureAwait(false);
        }

        _activeConsumers.Clear();

        // Exactly one side closes a connection: this exchange, or the caller that created it after the disposed flag
        // was set (see EnsureConnectedAsync). A connection that is still being created when this runs is therefore
        // closed by that caller, possibly after DisposeAsync has returned; that close is bounded by the same
        // limits as the one below.
        IConnection? connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            await CloseAndDisposeConnectionAsync(connection).ConfigureAwait(false);
        }

        // _connectionLock is intentionally not disposed: a SemaphoreSlim whose AvailableWaitHandle was never read
        // holds no unmanaged resource, and disposing it under a holder or a waiter turns their Release or WaitAsync
        // into an ObjectDisposedException. Waiters are woken by _disposeToken instead.
        _disposeCts.Dispose();
    }

    // Never throws: callers have already taken ownership of the connection, so a failure here must not stop them
    // from releasing anything else.
    private async Task CloseAndDisposeConnectionAsync(IConnection connection)
    {
        // The client gets ShutdownStepTimeout. abort: true because a graceful close (abort: false) raises any
        // timeout below 30 s to 30 s in RabbitMQ.Client; an abort still sends connection.close and waits for
        // the broker, but within the limit and without throwing on a connection that is already gone. The
        // client also swaps the token for None while the connection is open, so the token bounds the wait too.
        try
        {
            using var closeToken = new CancellationTokenSource(ConnectionCloseTokenTimeout, _timeProvider);
            await WaitBoundedAsync(
                    connection.CloseAsync(
                        Constants.ReplySuccess, "Goodbye", ShutdownStepTimeout, abort: true, closeToken.Token),
                    closeToken.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogConnectionCloseError(ex);
        }

        try
        {
            await DisposeBoundedAsync(connection.DisposeAsync()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogConnectionCloseError(ex);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_connection is { IsOpen: true })
        {
            return;
        }

        // Dispose wakes a caller that waits for the lock or is creating the connection. The caller's own token
        // is never replaced: a cancellation it requested still surfaces as OperationCanceledException.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeToken);
        try
        {
            await _connectionLock.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        try
        {
            // Re-check after acquiring the lock — the adapter may have been disposed or another caller may have
            // connected while this one waited.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (_connection is { IsOpen: true })
            {
                return;
            }

            var uri = new Uri(_options.ConnectionString);

            var factory = new ConnectionFactory
            {
                Uri = uri,
                AutomaticRecoveryEnabled = _options.AutomaticRecoveryEnabled,
                NetworkRecoveryInterval = _options.NetworkRecoveryInterval,
            };

            if (_options.ConfigureTls is not null)
            {
                string serverName = uri.Host;
                var tlsConfigurator = new RabbitMqTlsConfigurator();
                _options.ConfigureTls(tlsConfigurator);
                factory.Ssl = tlsConfigurator.Build(serverName);
            }
            else if (_options.SslOptions is not null)
            {
                factory.Ssl = _options.SslOptions;
            }
            else if (uri.Scheme.Equals("amqps", StringComparison.OrdinalIgnoreCase))
            {
                LogAmqpsWithoutTlsConfig();
            }

            IConnection connection;
            try
            {
                connection = await CreateConnection(factory, linked.Token).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _disposed) != 0)
            {
                // The client may wrap the cancellation raised by the dispose token (for example in
                // BrokerUnreachableException); the caller sees one exception type whatever the client threw.
                throw new ObjectDisposedException(GetType().FullName);
            }

            connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
            connection.RecoverySucceededAsync += OnRecoverySucceededAsync;

            // Publish, then re-check. Pairs with the CompareExchange on _disposed and the Interlocked.Exchange on
            // _connection in DisposeAsync: whichever side runs second takes the connection out of the field and
            // closes it, so there is exactly one owner and none is leaked.
            Interlocked.Exchange(ref _connection, connection);
            if (Volatile.Read(ref _disposed) != 0)
            {
                if (Interlocked.CompareExchange(ref _connection, null, connection) == connection)
                {
                    await CloseAndDisposeConnectionAsync(connection).ConfigureAwait(false);
                }

                throw new ObjectDisposedException(GetType().FullName);
            }

            LogConnectionEstablished(connection.Endpoint.HostName, connection.Endpoint.Port);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs args)
    {
        LogConnectionShutdown(args.Cause?.ToString() ?? "unknown", args.ReplyText ?? string.Empty);
        return Task.CompletedTask;
    }

    private Task OnRecoverySucceededAsync(object sender, AsyncEventArgs args)
    {
        LogRecoverySucceeded();
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ connection established to {Host}:{Port}.")]
    private partial void LogConnectionEstablished(string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ connection shut down. Cause: {Cause}. ReplyText: {ReplyText}.")]
    private partial void LogConnectionShutdown(string cause, string replyText);

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ connection recovery succeeded.")]
    private partial void LogRecoverySucceeded();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Cannot settle message {MessageId} ({Action}, delivery tag {DeliveryTag}): consumer channel " +
                  "'{ChannelId}' not found. The message stays unacknowledged on its original channel.")]
    private partial void LogSettlementChannelNotFound(
        string messageId, SettlementAction action, ulong deliveryTag, string? channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exception while closing RabbitMQ connection during dispose.")]
    private partial void LogConnectionCloseError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exception while closing publish channel.")]
    private partial void LogPublishChannelCloseError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exception while closing the topology channel.")]
    private partial void LogTopologyChannelCloseError(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Closing the topology channel was abandoned because the adapter is being disposed.")]
    private partial void LogTopologyChannelCloseAbandoned(Exception ex);

    // Guaranteed-routing return diagnostic. Logs only the exchange and routing key — both are topology
    // structure (the routing key on the send path is derived from the message type / an explicit mapping,
    // not from message content), never the body or headers. If a future routing-key scheme derives the key
    // from message content, that key would become sensitive and this log must be revisited (SEC).
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Publish returned as unroutable (guaranteed-routing mode): exchange '{Exchange}', " +
                  "routing key '{RoutingKey}' has no bound queue. Reported as not confirmed.")]
    private partial void LogPublishMessageReturned(string exchange, string routingKey);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Exception while closing consumer channel for endpoint '{EndpointName}'.")]
    private partial void LogConsumeChannelCloseError(string endpointName, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Exception while releasing consumer channel '{ChannelId}' for endpoint '{EndpointName}'.")]
    private partial void LogConsumerChannelReleaseError(string endpointName, string channelId, Exception ex);

    // Aggregated: reported once per shutdown for the FIRST failed requeue; the remaining buffered deliveries
    // were disposed without a nack and are returned by the broker when the channel closes. Logs identifiers
    // only, never headers or body.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to requeue buffered delivery {DeliveryTag} (message '{MessageId}') on endpoint " +
                  "'{EndpointName}' during consumer shutdown; {SkippedNacks} further buffered deliveries were " +
                  "disposed without a requeue and will be returned by the broker when the channel closes.")]
    private partial void LogShutdownDrainNackError(
        string endpointName, ulong deliveryTag, string messageId, int skippedNacks, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Cancelling the consumer before requeueing message '{MessageId}' failed; " +
                  "the requeue is sent anyway.")]
    private partial void LogRequeueCancelFailed(string messageId, Exception? ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Connection URI uses 'amqps://' but no TLS configuration was provided via ConfigureTls or SslOptions. " +
                  "The connection may fail if the broker requires TLS. " +
                  "Call ConfigureTls on RabbitMqTransportOptions to configure TLS explicitly.")]
    private partial void LogAmqpsWithoutTlsConfig();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Divergent per-type publish routing for message type '{MessageType}': {Dimension} was " +
                  "overwritten from '{PreviousValue}' to '{NewValue}'. Last-call-wins applies — '{NewValue}' " +
                  "is used at runtime; reconcile the conflicting registration calls (MapExchange/MapRoutingKey, " +
                  "DeclareExchange<T>, Publish<T>) to silence this warning.")]
    private partial void LogPublishRoutingDivergence(
        string messageType, string dimension, string previousValue, string newValue);
}
