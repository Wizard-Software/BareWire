using System.Buffers;
using System.Diagnostics;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Saga;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Core.FlowControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.Core.Bus;

/// <summary>
/// Runs a background consume loop for a single receive endpoint.
/// Reads messages from the transport via <see cref="ITransportAdapter.ConsumeAsync"/>,
/// deserializes each message, dispatches to the matching consumer, and settles (ACK/NACK).
/// </summary>
internal sealed partial class ReceiveEndpointRunner
{
    private readonly EndpointBinding _binding;
    private readonly ITransportAdapter _adapter;
    private readonly IMessageDeserializer _deserializer;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ISendEndpointProvider _sendEndpointProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FlowController _flowController;
    private readonly IBareWireInstrumentation _instrumentation;
    private readonly ILogger _logger;
    private readonly ConsumerInvokerFactory.InvokerDelegate[] _invokers;
    private readonly ConsumerInvokerFactory.RawInvokerDelegate[] _rawInvokers;
    private readonly ISagaMessageDispatcher[] _sagaDispatchers;

    internal ReceiveEndpointRunner(
        EndpointBinding binding,
        ITransportAdapter adapter,
        IMessageDeserializer deserializer,
        IPublishEndpoint publishEndpoint,
        ISendEndpointProvider sendEndpointProvider,
        IServiceScopeFactory scopeFactory,
        FlowController flowController,
        IBareWireInstrumentation instrumentation,
        ILogger logger,
        IReadOnlyList<ISagaMessageDispatcher>? sagaDispatchers = null)
    {
        _binding = binding;
        _adapter = adapter;
        _deserializer = deserializer;
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

        try
        {
            await foreach (InboundMessage message in _adapter
                .ConsumeAsync(_binding.EndpointName, flowControl, cancellationToken)
                .ConfigureAwait(false))
            {
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

                    // Start a consume activity for distributed tracing.
                    Guid msgId = Guid.TryParse(message.MessageId, out Guid parsed) ? parsed : Guid.Empty;
                    Activity? activity = _instrumentation.StartConsumeActivity(
                        messageType, _binding.EndpointName, msgId, message.Headers);

                    try
                    {
                        bool dispatched = false;

                        // Try typed consumers first — first match wins.
                        for (int i = 0; i < _invokers.Length; i++)
                        {
                            ConsumerInvokerFactory.InvokerDelegate invoker = _invokers[i];
                            try
                            {
                                await invoker(
                                    _scopeFactory,
                                    message.Body,
                                    message.Headers,
                                    message.MessageId,
                                    _publishEndpoint,
                                    _sendEndpointProvider,
                                    _deserializer,
                                    _binding.EndpointName,
                                    cancellationToken).ConfigureAwait(false);
                                messageType = _binding.Consumers[i].MessageType.Name;
                                dispatched = true;
                                break; // First matching consumer wins.
                            }
                            catch (Abstractions.Exceptions.UnknownPayloadException)
                            {
                                // This invoker's message type doesn't match — try the next one.
                                continue;
                            }
                            catch (Abstractions.Exceptions.BareWireSerializationException ex)
                            {
                                // Deserialization failed for this invoker — log and try the next one.
                                LogDeserializationFailed(_binding.EndpointName, message.MessageId, ex);
                                continue;
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
                                        message.Body,
                                        message.Headers,
                                        message.MessageId,
                                        _publishEndpoint,
                                        _sendEndpointProvider,
                                        _deserializer,
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
                                    LogDeserializationFailed(_binding.EndpointName, message.MessageId, ex);
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
                                    message.Body,
                                    message.Headers,
                                    message.MessageId,
                                    _publishEndpoint,
                                    _sendEndpointProvider,
                                    _deserializer,
                                    cancellationToken).ConfigureAwait(false);
                            }

                            messageType = "raw";
                            dispatched = true;
                        }

                        if (!dispatched)
                        {
                            LogNoConsumerMatched(_binding.EndpointName, message.MessageId);
                        }

                        action = dispatched ? SettlementAction.Ack : SettlementAction.Reject;

                        // Update activity tag now that messageType is resolved.
                        activity?.SetTag("messaging.message.type", messageType);

                        // Record successful consume metrics.
                        if (dispatched)
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

                    if (message.PooledBuffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(message.PooledBuffer);
                    }

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
}
