using System.Diagnostics;
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

                try
                {
                    SettlementAction action = SettlementAction.Nack;
                    string messageType = "unknown";
                    long startTimestamp = Stopwatch.GetTimestamp();
                    Guid msgId = Guid.TryParse(message.MessageId, out Guid parsed) ? parsed : Guid.Empty;

                    // Activity is started AFTER messageType is resolved to avoid "unknown" leaking
                    // to streaming exporters before dispatch completes.
                    Activity? activity = null;

                    try
                    {
                        var terminatorState = t_terminatorState ??= new TerminatorState();
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

                        if (!_binding.HasDeadLetterExchange)
                        {
                            LogMessageLostNoDlx(_binding.EndpointName, message.MessageId);
                        }
                    }
                    finally
                    {
                        activity?.Dispose();
                    }

                    try
                    {
                        // Use CancellationToken.None for requeue during cancellation — the requeue
                        // itself must not be cancelled, otherwise the message is silently lost.
                        CancellationToken settleCt = action == SettlementAction.Requeue
                            ? CancellationToken.None
                            : cancellationToken;
                        await _adapter.SettleAsync(action, message, settleCt).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogSettlementError(_binding.EndpointName, message.MessageId, action, ex);
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
    // Safe because each endpoint has a single-reader consume loop, fully awaited before next message.
    [ThreadStatic]
    private static TerminatorState? t_terminatorState;

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
