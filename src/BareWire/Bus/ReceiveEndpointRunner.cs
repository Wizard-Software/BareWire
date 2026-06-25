using System.Diagnostics;
using System.Threading.Channels;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Saga;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.FlowControl;
using BareWire.Pipeline;
using BareWire.Pipeline.Retry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.Bus;

/// <summary>
/// Runs a background consume loop for a single receive endpoint.
/// Reads messages from the transport via <see cref="ITransportAdapter.ConsumeAsync"/>,
/// deserializes each message, dispatches to the matching consumer, and settles (ACK/NACK).
/// </summary>
internal sealed partial class ReceiveEndpointRunner
{
    private readonly EndpointBinding _binding;
    private readonly ITransportAdapter _adapter;
    private readonly IConsumerChannelManager? _channelManager;
    private readonly IDeserializerResolver _deserializerResolver;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ISendEndpointProvider _sendEndpointProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FlowController _flowController;
    private readonly IBareWireInstrumentation _instrumentation;
    private readonly ILogger _logger;
    private readonly ConsumerInvokerFactory.InvokerDelegate[] _invokers;
    private readonly string[] _consumerMessageTypeNames;
    private readonly ConsumerInvokerFactory.RawInvokerDelegate[] _rawInvokers;
    private readonly ISagaMessageDispatcher[] _sagaDispatchers;
    private readonly MiddlewareChain _staticChain;
    private readonly bool _hasDiMiddleware;

    internal ReceiveEndpointRunner(
        EndpointBinding binding,
        ITransportAdapter adapter,
        IDeserializerResolver deserializerResolver,
        IPublishEndpoint publishEndpoint,
        ISendEndpointProvider sendEndpointProvider,
        IServiceScopeFactory scopeFactory,
        FlowController flowController,
        IBareWireInstrumentation instrumentation,
        ILogger logger,
        IReadOnlyList<ISagaMessageDispatcher>? sagaDispatchers = null,
        ILoggerFactory? loggerFactory = null)
    {
        _binding = binding;
        _adapter = adapter;
        _channelManager = adapter as IConsumerChannelManager;
        _deserializerResolver = deserializerResolver;
        _publishEndpoint = publishEndpoint;
        _sendEndpointProvider = sendEndpointProvider;
        _scopeFactory = scopeFactory;
        _flowController = flowController;
        _instrumentation = instrumentation;
        _logger = logger;

        // Build typed invokers once at startup — no reflection in the hot path.
        _invokers = binding.Consumers
            .Select(c => ConsumerInvokerFactory.Create(c.ConsumerType, c.MessageType))
            .ToArray();

        // Pre-compute message type names for header-based routing — zero allocations in hot path.
        // Mirrors SagaMessageDispatcher pattern (see SagaMessageDispatcher._eventTypeNames).
        _consumerMessageTypeNames = binding.Consumers
            .Select(c => c.MessageType.Name)
            .ToArray();

        // Build raw invokers once at startup — no reflection in the hot path.
        _rawInvokers = binding.RawConsumers
            .Select(ConsumerInvokerFactory.CreateRaw)
            .ToArray();

        // Wire saga dispatchers for the saga types registered on this endpoint.
        // sagaDispatchers contains ALL registered saga dispatchers; filter to those whose
        // StateMachineType is listed in binding.SagaTypes.
        if (sagaDispatchers is not null && binding.SagaTypes.Count > 0)
        {
            HashSet<Type> sagaTypeSet = [.. binding.SagaTypes];
            _sagaDispatchers = sagaDispatchers
                .Where(d => sagaTypeSet.Contains(d.StateMachineType))
                .ToArray();
        }
        else
        {
            _sagaDispatchers = [];
        }

        // Build retry/DLQ middleware chain (task 8.12).
        List<IMessageMiddleware> middlewares = [];

        if (binding.RetryCount > 0)
        {
            ILogger<RetryMiddleware> retryLogger = loggerFactory is not null
                ? loggerFactory.CreateLogger<RetryMiddleware>()
                : Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger<RetryMiddleware>();
            IntervalRetryPolicy retryPolicy = new(
                maxRetries: binding.RetryCount,
                interval: binding.RetryInterval,
                handledExceptions: [],
                ignoredExceptions: []);
            middlewares.Add(new RetryMiddleware(retryPolicy, retryLogger));
        }

        // DeadLetterMiddleware logs the error; re-throws so ReceiveEndpointRunner NACKs.
        ILogger<DeadLetterMiddleware> dlqLogger = loggerFactory is not null
            ? loggerFactory.CreateLogger<DeadLetterMiddleware>()
            : Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger<DeadLetterMiddleware>();
        middlewares.Add(new DeadLetterMiddleware(
            onDeadLetter: static (_, _) => Task.CompletedTask,
            dlqLogger));

        _staticChain = new MiddlewareChain(middlewares);

        // Probe once at startup to avoid per-message GetServices + ToArray allocation
        // when no DI middleware is registered (the common case).
        using (var probe = scopeFactory.CreateScope())
        {
            _hasDiMiddleware = probe.ServiceProvider.GetServices<IMessageMiddleware>().Any();
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        FlowControlOptions flowControl = new()
        {
            MaxInFlightMessages = _binding.PrefetchCount,
        };

        CreditManager creditManager = _flowController.GetOrCreateManager(
            _binding.EndpointName, flowControl);

        LogEndpointStarting(
            _binding.EndpointName,
            _binding.Consumers.Count + _binding.RawConsumers.Count + _sagaDispatchers.Length);

        // Captures the BW-ConsumerChannelId from the first message in the stream.
        // Used to release the consumer channel via IConsumerChannelManager after all
        // in-flight settlements are complete (normal cleanup path post-ConsumeAsync).
        string? consumerChannelId = null;

        // Per-key ordering (ADR-026): OFF by default. When _binding.Ordering is null the pump stays
        // strictly sequential and the [ThreadStatic] terminator pooling is used unchanged (byte-for-byte
        // identical to pre-per-key-ordering behavior). When non-null, ConcurrentMessageLimit becomes
        // load-bearing (C1) and a monotonic arrival sequence is assigned in this single reader before any
        // fan-out (C2); each lane carries its own TerminatorState (the [ThreadStatic] invariant no longer
        // holds across parallel lanes).
        bool orderingEnabled = _binding.Ordering is not null;
        OrderedDispatchStage? orderedStage = orderingEnabled
            ? new OrderedDispatchStage(this, creditManager, ResolveLaneCount(), _binding.Ordering!, cancellationToken)
            : null;

        // Monotonic arrival sequence — assigned in this single reader, in channel-read order, BEFORE any
        // fan-out. This is the only point with guaranteed ordering and is the FIFO anchor the local
        // partitioned layer (R8.6) builds per-key ordering on. A plain local long is correct because this
        // loop is the sole writer (single-reader invariant); no Interlocked needed.
        long arrivalSequence = 0;

        try
        {
            await foreach (InboundMessage message in _adapter
                .ConsumeAsync(_binding.EndpointName, flowControl, cancellationToken)
                .ConfigureAwait(false))
            {
                // Capture the channel ID from the first message — all messages on the same
                // ConsumeAsync stream share the same BW-ConsumerChannelId.
                consumerChannelId ??= message.Headers.TryGetValue("BW-ConsumerChannelId", out string? channelId)
                    ? channelId
                    : null;

                // Wait for credit (ADR-004: credit-based flow control).
                while (creditManager.TryGrantCredits(1) == 0)
                {
                    await creditManager.WaitForCreditAsync(cancellationToken).ConfigureAwait(false);
                }

                long bodyLength = message.Body.Length;
                creditManager.TrackInflightBytes(bodyLength);

                // Assign the arrival sequence AFTER credit and BEFORE fan-out (ADR-026 §1c). Snapshot it
                // into a local so the ordered path never closes over the mutating loop variable.
                long sequence = arrivalSequence++;

                if (orderedStage is not null)
                {
                    // Ordered path: fan out to a fixed lane. The lane owns its TerminatorState and runs its
                    // messages sequentially, so credit release / dispose / health-check happen on the lane
                    // when the message completes — not here. (R8.5 lane assignment is interim; fixed-lane
                    // key hashing lands in R8.6.)
                    await orderedStage.EnqueueAsync(message, sequence, bodyLength).ConfigureAwait(false);
                    continue;
                }

                // Sequential path (per-key ordering OFF): unchanged pre-per-key-ordering behavior, using the
                // [ThreadStatic] terminator pool. Fully awaited before the next message is read.
                // ProcessMessageAsync now returns SettlementOutcome; settlement is performed here
                // (same semantics as before — byte-for-byte identical to pre-R8.12 behavior).
                TerminatorState terminatorState = t_terminatorState ??= new TerminatorState();
                try
                {
                    SettlementOutcome outcome = await ProcessMessageAsync(
                            message, terminatorState, sequence, bodyLength, cancellationToken)
                        .ConfigureAwait(false);

                    // Sequential path: settle immediately (same as pre-R8.12).
                    // Log message-lost warning when NACKing with no DLX configured
                    // (same as pre-R8.12: the message will be permanently lost by the broker).
                    if (outcome.Action == SettlementAction.Nack && !_binding.HasDeadLetterExchange)
                    {
                        LogMessageLostNoDlx(_binding.EndpointName, message.MessageId);
                    }

                    try
                    {
                        CancellationToken settleCt = outcome.Action == SettlementAction.Requeue
                            ? CancellationToken.None
                            : cancellationToken;
                        await _adapter.SettleAsync(outcome.Action, message, settleCt).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogSettlementError(_binding.EndpointName, message.MessageId, outcome.Action, ex);
                    }
                }
                finally
                {
                    creditManager.ReleaseInflight(1, bodyLength);

                    message.Dispose();

                    BusStatus healthStatus = _flowController.CheckHealth(_binding.EndpointName);
                    if (healthStatus == BusStatus.Degraded)
                    {
                        LogFlowControlDegraded(_binding.EndpointName);
                    }
                }
            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogConsumeLoopCancelled(_binding.EndpointName);
        }
        catch (Exception ex)
        {
            LogConsumeLoopFaulted(_binding.EndpointName, ex);
            throw;
        }
        finally
        {
            // Drain in-flight lanes ALWAYS — on normal completion AND on cancellation/fault. CompleteAsync
            // completes the lane writers, so each lane's reader (which intentionally reads with
            // CancellationToken.None — see Lane.RunAsync) finishes draining its already-enqueued items
            // (settling + disposing them) and then exits; Task.WhenAll joins all lane workers. Skipping this
            // on cancellation would leak laneCount background tasks per endpoint and orphan in-flight
            // messages (never settled / disposed → pooled-buffer leak).
            if (orderedStage is not null)
            {
                await orderedStage.CompleteAsync().ConfigureAwait(false);
            }

            // Release the consumer channel so the broker can reclaim it.
            // CancellationToken.None is intentional — by the time we get here the original
            // cancellationToken is likely already cancelled (shutdown scenario), but the
            // close handshake with the broker must still complete cleanly.
            if (_channelManager is not null && consumerChannelId is not null)
            {
                await _channelManager
                    .ReleaseConsumerChannelAsync(consumerChannelId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Resolves the number of parallel dispatch lanes for the ordered path. Explicit
    /// <see cref="Abstractions.Configuration.IConsumerOrderingConfiguration.Concurrency"/> wins; otherwise
    /// falls back to the endpoint <see cref="EndpointBinding.ConcurrentMessageLimit"/> (C1 — the limit
    /// becomes load-bearing only on this path). Clamped to at least 1.
    /// </summary>
    private int ResolveLaneCount()
    {
        int configured = _binding.Ordering?.Concurrency ?? _binding.ConcurrentMessageLimit;
        return configured < 1 ? 1 : configured;
    }

    /// <summary>
    /// Processes and settles a single message through the middleware pipeline using the supplied
    /// terminator state. Shared by the sequential path (per-key ordering OFF, <see cref="t_terminatorState"/>)
    /// and the ordered path (per-key ordering ON, per-lane terminator state). The caller owns credit release,
    /// message disposal, and the health-check; this method owns dispatch, metrics/activity, and settlement.
    /// </summary>
    /// <remarks>
    /// <paramref name="arrivalSequence"/> is the monotonic FIFO sequence assigned in the single reader before
    /// fan-out (ADR-026 §1c / C2). R8.5 propagates it to the dispatch stage; the local partitioned layer
    /// (R8.6) consumes it to order messages within a lane. It is not surfaced on the public
    /// <see cref="MessageContext"/> — it is an implementation detail of the local ordering layer.
    /// </remarks>
    /// <summary>
    /// Returns the settlement decision after dispatching. Settlement itself (calling
    /// <see cref="ITransportAdapter.SettleAsync"/>) is the CALLER's responsibility:
    /// <list type="bullet">
    /// <item>Sequential path (ordering OFF): the caller settles immediately after receiving the
    /// outcome — semantics byte-for-byte identical to pre-R8.12.</item>
    /// <item>Ordered path (ordering ON): <c>Lane.RunAsync</c> routes the outcome through
    /// <see cref="PoisonContract.HandleOutcomeAsync"/>, which owns settlement and the
    /// poison/anti-starvation contract.</item>
    /// </list>
    /// </summary>
    private async ValueTask<SettlementOutcome> ProcessMessageAsync(
        InboundMessage message,
        TerminatorState terminatorState,
        long arrivalSequence,
        long bodyLength,
        CancellationToken cancellationToken)
    {
        _ = arrivalSequence; // Consumed by the local partitioned ordering layer (R8.6); see remarks.

        SettlementAction action = SettlementAction.Nack;
        string messageType = "unknown";
        long startTimestamp = Stopwatch.GetTimestamp();
        Guid msgId = Guid.TryParse(message.MessageId, out Guid parsed) ? parsed : Guid.Empty;

        // Activity is started AFTER messageType is resolved to avoid "unknown" leaking
        // to streaming exporters before dispatch completes.
        Activity? activity = null;

        try
        {
            terminatorState.Reset(this, cancellationToken);
            NextMiddleware terminator = terminatorState.InvokeAsync;

            // Build MessageContext for the middleware pipeline.
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            MessageContext context = new(
                messageId: msgId,
                headers: message.Headers,
                rawBody: message.Body,
                serviceProvider: scope.ServiceProvider,
                endpointName: _binding.EndpointName,
                cancellationToken: cancellationToken);

            // Resolve DI-registered middleware (e.g. TransactionalOutboxMiddleware).
            // DI middleware is scoped — must be resolved per-message from the current scope.
            // NOTE: TransactionalOutboxMiddleware wraps the entire processing including
            // retry, so DI middleware is placed BEFORE the static chain (Retry → DLQ).
            // This ensures the ambient TransactionScope from the outbox middleware is
            // active during both the initial attempt and any retry attempts.
            // Skip GetServices + ToArray when no DI middleware is registered (common case).
            IMessageMiddleware[] diMiddlewares = _hasDiMiddleware
                ? scope.ServiceProvider.GetServices<IMessageMiddleware>().ToArray()
                : [];

            // Build the full pipeline: DI middleware → static chain (Retry → DLQ) → terminator.
            // If no DI middleware is registered (typical case), invoke the static chain directly
            // to avoid per-message delegate allocations.
            if (diMiddlewares.Length == 0)
            {
                // Fast path: no DI middleware registered — no intermediate delegate.
                await _staticChain.InvokeAsync(context, terminator).ConfigureAwait(false);
            }
            else
            {
                // Wrap DI middleware around the static chain in FIFO order
                // (first registered = outermost = first to execute).
                NextMiddleware pipeline = WrapDiMiddleware(diMiddlewares, _staticChain, terminator);
                await pipeline(context).ConfigureAwait(false);
            }

            // Check inbox filter BEFORE "no consumer matched" logic.
            // HasItems checks null without triggering lazy dictionary allocation.
            bool inboxFiltered = context.HasItems
                && context.Items.TryGetValue(
                    Abstractions.Pipeline.WellKnownItemKeys.InboxFiltered, out object? filtered)
                && filtered is true;

            if (!terminatorState.Dispatched && !inboxFiltered)
            {
                LogNoConsumerMatched(_binding.EndpointName, message.MessageId);
            }

            action = (terminatorState.Dispatched || inboxFiltered)
                ? SettlementAction.Ack
                : SettlementAction.Reject;
            messageType = terminatorState.MessageType;

            // Start the activity now that messageType is fully resolved.
            activity = _instrumentation.StartConsumeActivity(
                messageType, _binding.EndpointName, msgId, message.Headers);

            // Record successful consume metrics.
            if (terminatorState.Dispatched)
            {
                double durationMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                _instrumentation.RecordConsume(
                    _binding.EndpointName, messageType, durationMs, (int)bodyLength);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            action = SettlementAction.Requeue;
        }
        catch (Exception ex)
        {
            // Start an error activity if one hasn't been created yet — messageType may
            // still be "unknown" here if the exception occurred before dispatch completed.
            activity ??= _instrumentation.StartConsumeActivity(
                messageType, _binding.EndpointName, msgId, message.Headers);
            LogConsumerError(_binding.EndpointName, message.MessageId, ex);
            _instrumentation.RecordFailure(
                _binding.EndpointName, messageType, ex.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            action = SettlementAction.Nack;
        }
        finally
        {
            activity?.Dispose();
        }

        return new SettlementOutcome(action, messageType);
    }

    private async Task<(bool Dispatched, string MessageType)> DispatchMessageAsync(
        MessageContext context,
        CancellationToken cancellationToken)
    {
        bool dispatched = false;
        string messageType = "unknown";
        string messageIdStr = context.MessageIdString;

        // --- Header-based routing (fast path) ---
        // When BW-MessageType header is present, route directly to the matching consumer
        // by type name. This avoids ambiguous deserialization of structurally similar
        // record types (e.g. PaymentEvent deserialized as OrderEvent by System.Text.Json).
        // Pattern: mirrors SagaMessageDispatcher.TryDispatchAsync() header-first routing.
        if (context.Headers.TryGetValue("BW-MessageType", out string? bwMessageType)
            && !string.IsNullOrEmpty(bwMessageType))
        {
            for (int i = 0; i < _invokers.Length; i++)
            {
                if (string.Equals(_consumerMessageTypeNames[i], bwMessageType, StringComparison.Ordinal))
                {
                    await _invokers[i](
                        _scopeFactory,
                        context.RawBody,
                        context.Headers,
                        messageIdStr,
                        _publishEndpoint,
                        _sendEndpointProvider,
                        _deserializerResolver,
                        _binding.EndpointName,
                        cancellationToken).ConfigureAwait(false);
                    messageType = _consumerMessageTypeNames[i];
                    dispatched = true;
                    break;
                }
            }
        }
        else
        {
            // --- Fallback: deserialization-based routing (legacy / raw interop) ---
            // When no BW-MessageType header is present, try each invoker sequentially;
            // first successful deserialization wins. Preserves backward compatibility
            // with external systems and raw interop scenarios.
            for (int i = 0; i < _invokers.Length; i++)
            {
                ConsumerInvokerFactory.InvokerDelegate invoker = _invokers[i];
                try
                {
                    await invoker(
                        _scopeFactory,
                        context.RawBody,
                        context.Headers,
                        messageIdStr,
                        _publishEndpoint,
                        _sendEndpointProvider,
                        _deserializerResolver,
                        _binding.EndpointName,
                        cancellationToken).ConfigureAwait(false);
                    messageType = _binding.Consumers[i].MessageType.Name;
                    dispatched = true;
                    break;
                }
                catch (Abstractions.Exceptions.UnknownPayloadException)
                {
                    // This invoker's message type doesn't match — try the next one.
                    continue;
                }
                catch (Abstractions.Exceptions.BareWireSerializationException ex)
                {
                    // Deserialization failed for this invoker — log and try the next one.
                    LogDeserializationFailed(_binding.EndpointName, messageIdStr, ex);
                    continue;
                }
            }
        }

        // If no typed consumer matched, try saga dispatchers.
        // Each dispatcher tries to deserialize the body as one of its registered event types.
        if (!dispatched && _sagaDispatchers.Length > 0)
        {
            foreach (ISagaMessageDispatcher sagaDispatcher in _sagaDispatchers)
            {
                try
                {
                    bool sagaHandled = await sagaDispatcher.TryDispatchAsync(
                        context.RawBody,
                        context.Headers,
                        messageIdStr,
                        _binding.EndpointName,
                        _publishEndpoint,
                        _sendEndpointProvider,
                        _deserializerResolver,
                        cancellationToken).ConfigureAwait(false);

                    if (sagaHandled)
                    {
                        messageType = sagaDispatcher.StateMachineType.Name;
                        dispatched = true;
                        break;
                    }
                }
                catch (Abstractions.Exceptions.BareWireSerializationException ex)
                {
                    LogDeserializationFailed(_binding.EndpointName, messageIdStr, ex);
                }
            }
        }

        // If no typed consumer or saga matched, fall through to raw consumers.
        // Raw consumers accept any payload — all registered raw consumers are invoked.
        if (!dispatched && _rawInvokers.Length > 0)
        {
            foreach (ConsumerInvokerFactory.RawInvokerDelegate rawInvoker in _rawInvokers)
            {
                await rawInvoker(
                    _scopeFactory,
                    context.RawBody,
                    context.Headers,
                    messageIdStr,
                    _publishEndpoint,
                    _sendEndpointProvider,
                    _deserializerResolver,
                    cancellationToken).ConfigureAwait(false);
            }

            messageType = "raw";
            dispatched = true;
        }

        return (dispatched, messageType);
    }

    private static NextMiddleware WrapDiMiddleware(
        IMessageMiddleware[] diMiddlewares,
        MiddlewareChain staticChain,
        NextMiddleware terminator)
    {
        NextMiddleware pipeline = ctx => staticChain.InvokeAsync(ctx, terminator);
        for (int i = diMiddlewares.Length - 1; i >= 0; i--)
        {
            IMessageMiddleware mw = diMiddlewares[i];
            NextMiddleware next = pipeline;
            pipeline = ctx => mw.InvokeAsync(ctx, next);
        }

        return pipeline;
    }

    // Thread-local pooling avoids ~40 B per-message allocation.
    // Safe on the SEQUENTIAL path (per-key ordering OFF) because that path is a single-reader consume loop
    // fully awaited before the next message. The ORDERED path (ADR-026) does NOT use this [ThreadStatic] —
    // parallel lanes would interleave it — and instead gives each lane its own TerminatorState
    // (see OrderedDispatchStage). The default-OFF invariant keeps this field byte-for-byte unchanged.
    [ThreadStatic]
    private static TerminatorState? t_terminatorState;

    /// <summary>
    /// The settlement decision returned by <see cref="ProcessMessageAsync"/>. A
    /// <see langword="readonly record struct"/> (no allocation per-message — ADR-003, PERF-2).
    /// Settlement (calling <see cref="ITransportAdapter.SettleAsync"/>) is the caller's
    /// responsibility; this type is the transport of the decision only.
    /// </summary>
    private readonly record struct SettlementOutcome(SettlementAction Action, string MessageType);

    private sealed class TerminatorState
    {
        private ReceiveEndpointRunner _runner = null!;
        private CancellationToken _ct;

        public bool Dispatched;
        public string MessageType = "unknown";

        internal void Reset(ReceiveEndpointRunner runner, CancellationToken ct)
        {
            _runner = runner;
            _ct = ct;
            Dispatched = false;
            MessageType = "unknown";
        }

        public async Task InvokeAsync(MessageContext ctx)
        {
            (Dispatched, MessageType) = await _runner.DispatchMessageAsync(ctx, _ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Bounded keyed-concurrency dispatch stage for the per-key ordering ON path (ADR-026 §1 — local layer).
    /// Holds a fixed set of <c>laneCount</c> lanes (C1: <c>laneCount</c> derives from
    /// <see cref="ResolveLaneCount"/>); the single reader fans a message out to one lane (this stage), and
    /// each lane processes its messages strictly sequentially with its OWN terminator state (so the
    /// <see cref="t_terminatorState"/> single-reader invariant is not violated across parallel lanes). The
    /// lane owns credit release, message disposal, and the health-check for the messages it runs.
    /// </summary>
    /// <remarks>
    /// The message→lane assignment is fixed-lane key hashing (R8.6): the ordering key is resolved in the
    /// single reader and hashed to one of the fixed lanes via <see cref="OrderingKeyResolver"/>, so the SAME
    /// key always lands on the SAME lane (the per-key ordering guarantee — ADR-026 §6, P3). Different keys may
    /// share a lane (partitioned model); keyless messages fall back to round-robin over the arrival sequence.
    /// Lane queues are bounded (axis 2 — ADR-026 §7, P2): each lane's channel has a depth of
    /// <c>ceil(PrefetchCount / laneCount)</c>, minimum 1. Lane workers drain independently of the
    /// single reader, so a full lane or exhausted credit (axis 1) causes a transient stall of the
    /// reader — not a deadlock, not permanent isolation loss. This is the accepted trade-off for
    /// ordering-ON (opt-in, default-OFF). The bound is on message count only, NOT bytes
    /// (<c>MaxInFlightBytes</c> does not gate intake — ADR-026 §7). Poison/anti-starvation per
    /// partition is R8.12.
    /// </remarks>
    private sealed class OrderedDispatchStage
    {
        private readonly ReceiveEndpointRunner _runner;
        private readonly CreditManager _creditManager;
        private readonly CancellationToken _cancellationToken;
        private readonly Lane[] _lanes;
        private readonly IConsumerOrderingConfiguration _ordering;
        private readonly MappingEpochTracker _epochTracker;

        internal OrderedDispatchStage(
            ReceiveEndpointRunner runner,
            CreditManager creditManager,
            int laneCount,
            IConsumerOrderingConfiguration ordering,
            CancellationToken cancellationToken)
        {
            _runner = runner;
            _creditManager = creditManager;
            _cancellationToken = cancellationToken;
            _ordering = ordering;

            int laneDepth = OrderedDispatchLaneDepth.Resolve(
                laneCount,
                runner._binding.PrefetchCount,
                configuredDepth: null);

            _lanes = new Lane[laneCount];
            for (int i = 0; i < laneCount; i++)
            {
                _lanes[i] = new Lane(this, laneDepth);
            }

            _epochTracker = new MappingEpochTracker(laneCount, runner._binding.EndpointName, runner._logger);
        }

        /// <summary>
        /// Routes a message to its fixed lane (R8.6 fixed-lane key hashing) and hands off ownership of
        /// credit release / disposal to that lane. Returns when the item is accepted by the lane channel
        /// (not when processing completes) — this is what enables cross-key parallelism while keeping
        /// per-lane FIFO order.
        /// </summary>
        /// <remarks>
        /// The ordering key is resolved from the message headers via <see cref="OrderingKeyResolver.Resolve"/>
        /// using the configured key source (header name / correlation-id / keyless). The key is then mapped
        /// to a stable lane index via <see cref="OrderingKeyResolver.ResolveLaneIndex"/>: the same key always
        /// maps to the same lane (fixed-lane affinity), ensuring that all messages sharing a key are processed
        /// sequentially by one lane worker.
        /// <para>
        /// Keyless messages (no resolved key) fall back to round-robin over the arrival sequence — they are
        /// distributed across lanes without ordering guarantees, preserving pre-per-key-ordering parallel
        /// throughput for unkeyed traffic.
        /// </para>
        /// <para>
        /// Lane channels are bounded (<c>BoundedChannelFullMode.Wait</c>). When a lane is full the
        /// single reader stalls on <c>WriteAsync</c> until a lane worker drains the head — backpressure
        /// that is transient, not a deadlock (workers drain independently of the reader). Under hot-key
        /// skew this causes a brief cross-lane head-of-line delay for other lanes while the reader is
        /// blocked writing to the full lane. The bound is on message count only, NOT bytes.
        /// Poison-message anti-starvation is R8.12.
        /// </para>
        /// <para>
        /// C4 re-map detection (R8.12): if the message carries a <c>BW-MappingEpoch</c> header, the
        /// resolved epoch is observed by <see cref="MappingEpochTracker"/> for the target lane. An epoch
        /// change triggers a Warning log (opaque token only — S2). No header = no detection (D2).
        /// </para>
        /// </remarks>
        internal async ValueTask EnqueueAsync(InboundMessage message, long arrivalSequence, long bodyLength)
        {
            // R8.6: resolve the ordering key from headers and map to a FIXED lane.
            // The same key always maps to the same lane (key→lane affinity), so messages sharing a key
            // are queued into one lane channel and processed sequentially — preserving per-key FIFO order.
            // Key value is NOT logged or thrown (SEC S1/S2 discipline — ADR-026 §NIE WOLNO).
            // The raw key never leaves this method except to ResolveLaneIndex (hash) and, on a re-map
            // change, to MappingEpochTracker.Observe where it is immediately converted to an opaque token.
            // Enforced by OrderingSecurityTests.
            string? key = OrderingKeyResolver.Resolve(_ordering, message.Headers);
            int laneIndex = OrderingKeyResolver.ResolveLaneIndex(key, arrivalSequence, _lanes.Length);

            // C4 (R8.12): observe mapping epoch for the resolved lane — detect consistent-hash re-maps.
            // Resolving key once here satisfies PERF-2/SEC-dedup: the key lives in ONE place on this
            // guaranteed-order path and is never stored beyond this call (OpaqueToken on change only).
            // Non-allocating hot path: TryGetValue on Dictionary<string,string> + long.TryParse(string).
            // ToOpaqueToken is called only on the cold re-map-change path.
            if (message.Headers.TryGetValue(MappingEpochTracker.MappingEpochHeaderName, out string? epochStr)
                && long.TryParse(epochStr, out long epoch))
            {
                _epochTracker.Observe(laneIndex, epoch, key);
            }

            Lane lane = _lanes[laneIndex];
            await lane.Channel.Writer
                .WriteAsync(new WorkItem(message, arrivalSequence, bodyLength), _cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>Completes all lane channels and awaits in-flight lane drain.</summary>
        internal async Task CompleteAsync()
        {
            foreach (Lane lane in _lanes)
            {
                lane.Channel.Writer.Complete();
            }

            await Task.WhenAll(Array.ConvertAll(_lanes, static l => l.Worker)).ConfigureAwait(false);
        }

        private readonly record struct WorkItem(InboundMessage Message, long ArrivalSequence, long BodyLength);


        private sealed class Lane
        {
            private readonly OrderedDispatchStage _stage;

            // Per-lane terminator state: each lane runs its items sequentially, so one instance per lane is
            // safe and keeps allocation at O(laneCount), not O(messages) — ADR-003 (no per-message alloc).
            private readonly TerminatorState _terminatorState = new();

            // Per-lane poison contract (R8.12). Allocation is O(laneCount). State is per-head within
            // this lane — reset when the head MessageId changes or when a non-Nack outcome is received.
            private readonly PoisonContract _poisonContract;

            internal Lane(OrderedDispatchStage stage, int laneDepth)
            {
                _stage = stage;
                _poisonContract = new PoisonContract(
                    stage._runner._binding,
                    stage._runner._adapter,
                    stage._runner._logger);

                // BoundedChannelFullMode.Wait is the only correct mode: Drop* modes would lose messages,
                // breaking per-lane FIFO order and at-least-once delivery. The single reader stalls on
                // WriteAsync when the lane is full; a lane worker draining the head unblocks the reader
                // (workers run on Task.Run independently — no deadlock; see OrderedDispatchLaneDepth).
                Channel = System.Threading.Channels.Channel.CreateBounded<WorkItem>(
                    new BoundedChannelOptions(laneDepth)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = true,
                        SingleWriter = true,
                    });
                Worker = Task.Run(RunAsync);
            }

            internal System.Threading.Channels.Channel<WorkItem> Channel { get; }
            internal Task Worker { get; }

            private async Task RunAsync()
            {
                ReceiveEndpointRunner runner = _stage._runner;
                CreditManager creditManager = _stage._creditManager;
                CancellationToken ct = _stage._cancellationToken;

                try
                {
                    await foreach (WorkItem item in Channel.Reader.ReadAllAsync(CancellationToken.None)
                        .ConfigureAwait(false))
                    {
                        try
                        {
                            SettlementOutcome outcome = await runner.ProcessMessageAsync(
                                    item.Message, _terminatorState, item.ArrivalSequence, item.BodyLength, ct)
                                .ConfigureAwait(false);

                            // Ordered path: delegate settlement + poison contract to PoisonContract.
                            // Returns true = advance (release); false = C3 head retained (do not advance).
                            //
                            // C3 invariant (ADR-026 §7): the lane MUST NOT advance to the next WorkItem
                            // until IsDurablyConfirmed == true. When HandleOutcomeAsync returns false
                            // (all park-retry attempts exhausted without confirmation), the lane loops
                            // here, retrying HandleOutcomeAsync on the SAME message until either:
                            //   (a) the park eventually succeeds (IsDurablyConfirmed == true) → advance; or
                            //   (b) cancellation is requested → the outer loop exits and the lane drains.
                            //
                            // Credit is NOT released in the C3 loop — it is released in the outer finally
                            // only after the loop exits. Broker re-delivery credits are not involved here
                            // because this is a park-retry loop (not a broker re-delivery cycle). The
                            // in-flight message is intentionally held: releasing credit prematurely would
                            // allow the reader to fetch the next message and enqueue it, at which point the
                            // lane would advance to it — violating the C3 head-of-line ordering guarantee.
                            bool advance = await _poisonContract.HandleOutcomeAsync(
                                    outcome.Action, item.Message, runner._adapter, ct)
                                .ConfigureAwait(false);

                            // C3 loop: park failed — retry on the same head until confirmed or cancelled.
                            // A small delay prevents hot-spin when the park endpoint is persistently down
                            // and ParkDurablyAsync completes synchronously (e.g. in-memory test doubles).
                            while (!advance && !ct.IsCancellationRequested)
                            {
                                await Task.Delay(PoisonContract.C3RetryDelay, ct).ConfigureAwait(false);
                                advance = await _poisonContract.HandleOutcomeAsync(
                                        outcome.Action, item.Message, runner._adapter, ct)
                                    .ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            creditManager.ReleaseInflight(1, item.BodyLength);

                            item.Message.Dispose();

                            BusStatus healthStatus = runner._flowController.CheckHealth(runner._binding.EndpointName);
                            if (healthStatus == BusStatus.Degraded)
                            {
                                runner.LogFlowControlDegraded(runner._binding.EndpointName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // A lane worker must never fault silently — log and let CompleteAsync observe completion.
                    runner.LogConsumeLoopFaulted(runner._binding.EndpointName, ex);
                    throw;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Starting consume loop for endpoint '{EndpointName}' with {ConsumerCount} consumer(s).")]
    private partial void LogEndpointStarting(string endpointName, int consumerCount);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Error processing message {MessageId} on endpoint '{EndpointName}'.")]
    private partial void LogConsumerError(string endpointName, string messageId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Error settling message {MessageId} on endpoint '{EndpointName}' with action {Action}.")]
    private partial void LogSettlementError(string endpointName, string messageId, SettlementAction action, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Flow control degraded on endpoint '{EndpointName}' — approaching capacity.")]
    private partial void LogFlowControlDegraded(string endpointName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Deserialization failed for message {MessageId} on endpoint '{EndpointName}' — trying next consumer.")]
    private partial void LogDeserializationFailed(string endpointName, string messageId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No consumer matched message {MessageId} on endpoint '{EndpointName}' — message will be rejected.")]
    private partial void LogNoConsumerMatched(string endpointName, string messageId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Consume loop cancelled for endpoint '{EndpointName}'.")]
    private partial void LogConsumeLoopCancelled(string endpointName);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Consume loop faulted for endpoint '{EndpointName}'. The endpoint has stopped consuming.")]
    private partial void LogConsumeLoopFaulted(string endpointName, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Message {MessageId} on endpoint '{EndpointName}' will be permanently lost — " +
                  "no dead-letter exchange configured on the queue. " +
                  "Consider adding x-dead-letter-exchange to the queue declaration or configuring RetryCount.")]
    private partial void LogMessageLostNoDlx(string endpointName, string messageId);
}
