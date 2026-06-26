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
using BareWire.Routing;
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

    // ── Consume-time routing-key dispatch (ADR-030) ──────────────────────────────
    // Stateless, zero-alloc matcher — a single shared instance is safe because the matcher holds no
    // per-delivery state (IsMatch/CompareSpecificity operate only on pre-compiled inputs). Immutable,
    // so it does not violate the "no static mutable state" rule. Typed as the concrete
    // TopicPatternMatcher (not the ITopicMatcher seam) so the hot-path calls devirtualize (CA1859);
    // the ITopicMatcher abstraction still exists for testability and future per-transport strategy.
    private static readonly TopicPatternMatcher s_topicMatcher = new();

    // Per-consumer compiled topic patterns, indexed 1:1 with _invokers / _binding.Consumers.
    // An empty inner array means the consumer is a catch-all (selected by message type alone, D8).
    private readonly CompiledTopicPattern[][] _consumerPatterns;

    // Per-consumer AcceptUntyped opt-in, indexed 1:1 with _invokers. Part of the _hasAnyRoutingKeys
    // guard now; the type-less dispatch layer (a later task) uses it for candidacy.
    private readonly bool[] _consumerAcceptUntyped;

    // Guard (ADR-FIX-2): true when ANY consumer declares >=1 routing-key pattern OR AcceptUntyped().
    // When false, DispatchMessageAsync takes the bit-identical pre-ADR-030 legacy path, so endpoints
    // without any routing keys behave exactly as before (per-delivery cost unchanged). The name keeps
    // the task vocabulary, but the flag also accounts for AcceptUntyped() per ADR-FIX-2.
    private readonly bool _hasAnyRoutingKeys;

    // O(1) early-out for the type-less dispatch path (layer 3, 17.9): true when ANY consumer has
    // AcceptUntyped()==true. Mirrors _hasAnyRoutingKeys; computed once at startup so SelectUntypedAsync
    // short-circuits without scanning _invokers[] on every type-less delivery when no consumer opted in.
    private readonly bool _hasAnyAcceptUntyped;

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

        // Pre-compile per-consumer topic patterns once at Build()-time (allocation allowed here;
        // the hot path stays zero-alloc). Indexed 1:1 with _invokers / binding.Consumers. A consumer
        // with no routing keys gets an empty array and is treated as a catch-all (D8).
        _consumerPatterns = binding.Consumers
            .Select(c => c.RoutingKeys is { Count: > 0 } keys
                ? keys.Select(s_topicMatcher.Compile).ToArray()
                : Array.Empty<CompiledTopicPattern>())
            .ToArray();

        _consumerAcceptUntyped = binding.Consumers
            .Select(c => c.AcceptUntyped)
            .ToArray();

        // PERF-1: O(1) early-out for SelectUntypedAsync — computed once at startup, mirrors _hasAnyRoutingKeys.
        _hasAnyAcceptUntyped = Array.Exists(_consumerAcceptUntyped, static x => x);

        // Guard (ADR-FIX-2): the new pattern-aware dispatch path activates only when at least one
        // consumer declares routing-key patterns or opts into untyped delivery; otherwise the
        // bit-identical legacy path runs.
        _hasAnyRoutingKeys =
            Array.Exists(_consumerPatterns, static p => p.Length > 0)
            || Array.Exists(_consumerAcceptUntyped, static x => x);

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

                // 17.10: record the silently-unhandled delivery as a metric instead of leaving it invisible.
                // Raw BW-RoutingKey is NEVER passed (ADR-030 §Security / routing-key-in-logs decision);
                // messageType for an unhandled delivery is intrinsically "unknown" (terminatorState.MessageType).
                _instrumentation.RecordFailure(
                    _binding.EndpointName, terminatorState.MessageType, UnhandledDeliveryReason);
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
        bool dispatched;
        string messageType;
        string messageIdStr = context.MessageIdString;

        // First stage — typed-consumer selection. The guard (ADR-FIX-2) routes endpoints that declare
        // no routing-key patterns and no AcceptUntyped() to the bit-identical pre-ADR-030 legacy path;
        // endpoints with the feature active take the pattern-aware path (ADR-030 D4 layers 1-2).
        if (!_hasAnyRoutingKeys)
        {
            (dispatched, messageType) = await SelectTypedLegacyAsync(context, messageIdStr, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            (dispatched, messageType) = await SelectTypedWithPatternsAsync(context, messageIdStr, cancellationToken)
                .ConfigureAwait(false);
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

    /// <summary>
    /// Bit-identical pre-ADR-030 typed selection: the BW-MessageType fast path (break-on-first by
    /// type name) plus the legacy blind sequential-deserialize fallback for type-less deliveries.
    /// Reached only when <see cref="_hasAnyRoutingKeys"/> is <see langword="false"/> (ADR-FIX-2 —
    /// endpoints with no routing keys and no <c>AcceptUntyped()</c> behave exactly as before).
    /// </summary>
    private async Task<(bool Dispatched, string MessageType)> SelectTypedLegacyAsync(
        MessageContext context,
        string messageIdStr,
        CancellationToken cancellationToken)
    {
        bool dispatched = false;
        string messageType = "unknown";

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

        return (dispatched, messageType);
    }

    /// <summary>
    /// Pattern-aware typed selection (ADR-030 D4 layers 1-2). Reached only when
    /// <see cref="_hasAnyRoutingKeys"/> is <see langword="true"/>.
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Layer 1</b> — among consumers of the resolved type whose pattern set matches the
    ///     delivery's <c>BW-RoutingKey</c>, the most specific matched pattern wins (D5 metric;
    ///     first-registered on an unresolvable tie, plus a warning).
    ///   </description></item>
    ///   <item><description>
    ///     <b>Layer 2</b> — no pattern matched → the type's first-registered catch-all consumer
    ///     (empty pattern set) takes over; a warning fires when the delivery carried a routing key,
    ///     nothing matched, and no catch-all exists.
    ///   </description></item>
    /// </list>
    /// When <c>BW-MessageType</c> is absent (type-less delivery), this delegates to
    /// <see cref="SelectUntypedAsync"/> (layer 3, 17.9) — the delivery is routed by routing-key
    /// pattern to a consumer that has opted in via <c>AcceptUntyped()</c>; if no candidate exists it
    /// falls through to the shared saga/raw handling. The raw routing key is never written to logs
    /// (ADR-030 §Security — it is producer-controlled, untrusted input).
    /// </summary>
    private async Task<(bool Dispatched, string MessageType)> SelectTypedWithPatternsAsync(
        MessageContext context,
        string messageIdStr,
        CancellationToken cancellationToken)
    {
        if (!context.Headers.TryGetValue("BW-MessageType", out string? bwMessageType)
            || string.IsNullOrEmpty(bwMessageType))
        {
            // Type-less delivery — delegate to layer 3 (type-less raw-first dispatch, ADR-030 D4 §3, 17.9).
            return await SelectUntypedAsync(context, messageIdStr, cancellationToken).ConfigureAwait(false);
        }

        // D-V1: read BW-RoutingKey via TryGetValue — the IReadOnlyDictionary indexer throws
        // KeyNotFoundException when the header is absent (e.g. non-RabbitMQ transports). The value is
        // already a string, so AsSpan() over it allocates nothing on the hot path.
        context.Headers.TryGetValue("BW-RoutingKey", out string? routingKeyValue);
        ReadOnlySpan<char> routingKey = (routingKeyValue ?? string.Empty).AsSpan();

        // ── Layer 1: most specific matched pattern across consumers of this type (zero-alloc scan) ──
        // A pairwise scan reusing CompareSpecificity is the only zero-alloc option — building a
        // ReadOnlySpan<CompiledTopicPattern> of candidates for SelectMostSpecific would require a
        // managed-type stackalloc (CS0208) or a per-delivery list allocation (violates the 0-B/op gate).
        int bestIdx = -1;
        CompiledTopicPattern bestPattern = default;
        bool unresolvedTie = false;

        for (int i = 0; i < _invokers.Length; i++)
        {
            if (!string.Equals(_consumerMessageTypeNames[i], bwMessageType, StringComparison.Ordinal))
            {
                continue;
            }

            CompiledTopicPattern[] patterns = _consumerPatterns[i];
            if (patterns.Length == 0)
            {
                continue; // catch-all does not participate in layer 1
            }

            // Most specific of THIS consumer's own patterns that actually match the routing key.
            int localMatch = -1;
            CompiledTopicPattern localBest = default;
            for (int p = 0; p < patterns.Length; p++)
            {
                if (!s_topicMatcher.IsMatch(in patterns[p], routingKey))
                {
                    continue;
                }

                if (localMatch == -1 || s_topicMatcher.CompareSpecificity(in patterns[p], in localBest) > 0)
                {
                    localMatch = p;
                    localBest = patterns[p];
                }
            }

            if (localMatch == -1)
            {
                continue; // this consumer has no matching pattern
            }

            // Cross-consumer comparison: most-specific-wins, first-registered on tie.
            if (bestIdx == -1)
            {
                bestIdx = i;
                bestPattern = localBest;
                unresolvedTie = false;
            }
            else
            {
                int cmp = s_topicMatcher.CompareSpecificity(in localBest, in bestPattern);
                if (cmp > 0)
                {
                    bestIdx = i;
                    bestPattern = localBest;
                    unresolvedTie = false;
                }
                else if (cmp == 0)
                {
                    unresolvedTie = true; // first-registered wins (bestIdx unchanged)
                }
            }
        }

        if (bestIdx != -1)
        {
            if (unresolvedTie)
            {
                LogAmbiguousRoutingKeyMatch(_binding.EndpointName, messageIdStr);
            }

            await _invokers[bestIdx](
                _scopeFactory,
                context.RawBody,
                context.Headers,
                messageIdStr,
                _publishEndpoint,
                _sendEndpointProvider,
                _deserializerResolver,
                _binding.EndpointName,
                cancellationToken).ConfigureAwait(false);
            return (true, _consumerMessageTypeNames[bestIdx]);
        }

        // ── Layer 2: no pattern matched → first-registered catch-all of this type ──
        for (int i = 0; i < _invokers.Length; i++)
        {
            if (string.Equals(_consumerMessageTypeNames[i], bwMessageType, StringComparison.Ordinal)
                && _consumerPatterns[i].Length == 0)
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
                return (true, _consumerMessageTypeNames[i]);
            }
        }

        // No matched pattern and no catch-all. Warn only when a routing key was present (the producer
        // intended routing but nothing handled it). The raw key is NOT logged (ADR-030 §Security).
        if (!routingKey.IsEmpty)
        {
            LogNoRoutingKeyMatch(_binding.EndpointName, messageIdStr);
        }

        return (false, "unknown");
    }

    /// <summary>
    /// Maximum accepted raw-body size (in bytes) for the type-less dispatch path (layer 3) before
    /// deserialization is attempted. Secure-by-default v1 internal constant — the payload is
    /// producer-controlled and untrusted on this path; enforcing a cap prevents large-payload DoS.
    /// Per-endpoint configurability is deferred to 17.11 (trust-boundary type-less gate).
    /// </summary>
    private const long MaxUntypedPayloadBytes = 1L * 1024 * 1024; // 1 MiB

    // 17.10 (ADR-030 D4 layer 4): error_type tag category for the silently-unhandled-delivery metric.
    // Reuses the existing failed-message counter via IBareWireInstrumentation.RecordFailure — no new
    // public API / approved.txt change. Stable metric-tag contract (locked by tests).
    private const string UnhandledDeliveryReason = "UnhandledDelivery";

    /// <summary>
    /// Layer 3 type-less raw-first dispatch (ADR-030 D4 §3, 17.9). Reached only when
    /// <c>BW-MessageType</c> is absent AND <see cref="_hasAnyRoutingKeys"/> is
    /// <see langword="true"/> (guard ADR-FIX-2 routes here via
    /// <see cref="SelectTypedWithPatternsAsync"/>).
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Candidacy (ADR-FIX-1, secure-by-default):</b> a consumer is a candidate only when
    ///     <c>AcceptUntyped()</c> was declared (<c>_consumerAcceptUntyped[i] == true</c>) AND the
    ///     consumer has at least one routing-key pattern (<c>_consumerPatterns[i].Length &gt; 0</c>).
    ///     Catch-all consumers (empty pattern set) and consumers without <c>AcceptUntyped()</c> are
    ///     never candidates — this is the primary security gate.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Selection:</b> most-specific-wins via the same zero-alloc pairwise
    ///     <see cref="TopicPatternMatcher.CompareSpecificity"/> scan as layer 1; first-registered on
    ///     an unresolvable tie (plus a warning). No LINQ, no closures, no per-delivery allocation.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Type-less hardening:</b> payload size is checked against
    ///     <see cref="MaxUntypedPayloadBytes"/> BEFORE deserialization; oversized payloads fall
    ///     through without deserialization. The raw-first deserializer path
    ///     (<c>SystemTextJsonRawDeserializer</c>) uses <c>BareWireJsonSerializerOptions.Default</c>
    ///     which carries no polymorphic <c>TypeInfoResolver</c> and the default STJ
    ///     <c>MaxDepth</c> (64). These are guarantees on the JSON raw-deserializer path only; a
    ///     non-JSON content-type resolves through <c>_deserializerResolver</c> per its own rules.
    ///     Semantic shape validation (SEC-13 <c>SchemaValidationMiddleware</c>) is deferred to 17.11
    ///     (GAP-1/GAP-3); residual type-confusion risk is documented in ADR-030 (l. 136).
    ///   </description></item>
    ///   <item><description>
    ///     <b>Deserialization errors (ADR-001 raw-first):</b>
    ///     <see cref="Abstractions.Exceptions.UnknownPayloadException"/> (null payload) or
    ///     <see cref="Abstractions.Exceptions.BareWireSerializationException"/> (malformed JSON /
    ///     exceeded <c>MaxDepth</c>) → <c>(false, "unknown")</c> → falls through to layer 4
    ///     (saga/raw → undispatched + Reject). Exception message is NOT logged (SEC-2 / decision #4
    ///     — <c>JsonException.Message</c> can embed foreign JSON fragments); only the type name is
    ///     recorded.
    ///   </description></item>
    /// </list>
    /// </summary>
    private async Task<(bool Dispatched, string MessageType)> SelectUntypedAsync(
        MessageContext context,
        string messageIdStr,
        CancellationToken cancellationToken)
    {
        // PERF-1: O(1) early-out — avoids full scan when no consumer has opted in.
        if (!_hasAnyAcceptUntyped)
        {
            return (false, "unknown");
        }

        // BW-RoutingKey: producer-controlled, untrusted input — NEVER logged as a raw value
        // (ADR-030 §Security). AsSpan() over the existing string is zero-alloc.
        context.Headers.TryGetValue("BW-RoutingKey", out string? routingKeyValue);
        ReadOnlySpan<char> routingKey = (routingKeyValue ?? string.Empty).AsSpan();

        // ── Most-specific-wins pairwise scan (zero-alloc, mirrors layer 1) ──
        // A pairwise scan reusing CompareSpecificity is the only zero-alloc option — building a
        // ReadOnlySpan<CompiledTopicPattern> of candidates would require managed-type stackalloc
        // (CS0208) or a per-delivery list allocation (violates the 0-B/op gate). Same rationale as
        // the layer 1 comment above.
        int bestIdx = -1;
        CompiledTopicPattern bestPattern = default;
        bool unresolvedTie = false;

        for (int i = 0; i < _invokers.Length; i++)
        {
            // ADR-FIX-1 — secure-by-default candidacy: both conditions required.
            if (!_consumerAcceptUntyped[i])
            {
                continue; // consumer did not opt in to type-less delivery
            }

            CompiledTopicPattern[] patterns = _consumerPatterns[i];
            if (patterns.Length == 0)
            {
                continue; // D8: catch-all (empty pattern set) does not participate in layer 3
            }

            // Most specific of THIS consumer's own patterns that actually match the routing key.
            int localMatch = -1;
            CompiledTopicPattern localBest = default;
            for (int p = 0; p < patterns.Length; p++)
            {
                if (!s_topicMatcher.IsMatch(in patterns[p], routingKey))
                {
                    continue;
                }

                if (localMatch == -1 || s_topicMatcher.CompareSpecificity(in patterns[p], in localBest) > 0)
                {
                    localMatch = p;
                    localBest = patterns[p];
                }
            }

            if (localMatch == -1)
            {
                continue; // no pattern on this consumer matched the routing key
            }

            // Cross-consumer comparison: most-specific-wins, first-registered on tie.
            if (bestIdx == -1)
            {
                bestIdx = i;
                bestPattern = localBest;
                unresolvedTie = false;
            }
            else
            {
                int cmp = s_topicMatcher.CompareSpecificity(in localBest, in bestPattern);
                if (cmp > 0)
                {
                    bestIdx = i;
                    bestPattern = localBest;
                    unresolvedTie = false;
                }
                else if (cmp == 0)
                {
                    unresolvedTie = true; // first-registered wins (bestIdx unchanged)
                }
            }
        }

        if (bestIdx == -1)
        {
            return (false, "unknown"); // no candidate — falls through to layer 4 (saga/raw → Reject)
        }

        if (unresolvedTie)
        {
            // Raw routing-key NOT passed to the logger (ADR-030 §Security).
            LogAmbiguousRoutingKeyMatch(_binding.EndpointName, messageIdStr);
        }

        // ── Type-less hardening: enforce payload size limit BEFORE deserialization ──
        // ReadOnlySequence<byte>.Length is O(1) and alloc-free (derived from SequencePosition
        // indices). The check is on this path only — typed deliveries carry BW-MessageType set by
        // a BareWire producer (trusted), so a per-message cap is not applied there.
        if (context.RawBody.Length > MaxUntypedPayloadBytes)
        {
            // Raw routing-key and payload contents NOT logged (ADR-030 §Security).
            LogUntypedPayloadTooLarge(_binding.EndpointName, messageIdStr);
            return (false, "unknown"); // oversized → no deserialization → layer 4 (Reject)
        }

        // ADR-001 raw-first: deserialize the untrusted foreign payload to the candidate consumer's
        // declared TMessage. BareWireJsonSerializerOptions.Default (used by SystemTextJsonRawDeserializer)
        // has no TypeInfoResolver (no polymorphic dispatch) and the default STJ MaxDepth (64).
        try
        {
            await _invokers[bestIdx](
                _scopeFactory,
                context.RawBody,
                context.Headers,
                messageIdStr,
                _publishEndpoint,
                _sendEndpointProvider,
                _deserializerResolver,
                _binding.EndpointName,
                cancellationToken).ConfigureAwait(false);

            return (true, _consumerMessageTypeNames[bestIdx]);
        }
        catch (Abstractions.Exceptions.UnknownPayloadException)
        {
            // Deserializer returned null (empty/unrecognisable payload) → not dispatched → layer 4.
            return (false, "unknown");
        }
        catch (Abstractions.Exceptions.BareWireSerializationException ex)
        {
            // SEC-2 / decision #4: log ONLY the exception type name — NOT the full exception or its
            // message, because JsonException.Message can embed foreign JSON path/token fragments.
            LogUntypedDeserializationFailed(_binding.EndpointName, messageIdStr, ex.GetType().Name);
            return (false, "unknown");
        }
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

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No routing-key pattern matched and no catch-all consumer on endpoint '{EndpointName}' " +
                  "for message {MessageId} — message left undispatched.")]
    private partial void LogNoRoutingKeyMatch(string endpointName, string messageId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Ambiguous routing-key match (unresolvable specificity tie) on endpoint '{EndpointName}' " +
                  "for message {MessageId} — first-registered consumer selected.")]
    private partial void LogAmbiguousRoutingKeyMatch(string endpointName, string messageId);

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

    // Layer 3 (type-less dispatch) loggers — adhere to ADR-030 §Security: no raw routing-key or
    // payload content in any message; only endpoint name + message id (+ sanitised type name).

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Type-less payload for message {MessageId} on endpoint '{EndpointName}' " +
                  "exceeds the maximum allowed size — delivery falls through without deserialization.")]
    private partial void LogUntypedPayloadTooLarge(string endpointName, string messageId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Type-less deserialization rejected ({ExceptionType}) for message {MessageId} " +
                  "on endpoint '{EndpointName}' — delivery falls through.")]
    private partial void LogUntypedDeserializationFailed(string endpointName, string messageId, string exceptionType);
}
