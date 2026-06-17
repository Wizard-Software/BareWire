using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AzureServiceBus.Internal;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.AzureServiceBus;

/// <summary>
/// Consumer side of the Azure Service Bus transport adapter.
/// Implements <see cref="ITransportAdapter.ConsumeAsync"/> and
/// <see cref="ITransportAdapter.SettleAsync"/> (R2.1 + R2.2 sessions).
/// </summary>
internal sealed partial class AzureServiceBusTransportAdapter
{
    private readonly AzureServiceBusConsumerRegistry _consumerRegistry = new();

    // R2.2: tracks active session consumers for disposal on DisposeAsync.
    private readonly List<AzureServiceBusSessionConsumer> _sessionConsumers = [];
    private readonly object _sessionConsumersLock = new();

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// When <c>_options.EnableSessions</c> is <see langword="false"/> (default), builds a plain
    /// <see cref="ServiceBusReceiver"/> in PeekLock mode (R2.1 path), starts an
    /// <see cref="AzureServiceBusConsumer"/> polling loop, and exposes received messages as
    /// an <see cref="IAsyncEnumerable{T}"/> via a bounded <see cref="Channel{T}"/> (ADR-004).
    /// </para>
    /// <para>
    /// When <c>_options.EnableSessions</c> is <see langword="true"/> (R2.2 path), builds an
    /// <see cref="AzureServiceBusSessionConsumer"/> that accepts sessions via
    /// <c>AcceptNextSessionAsync</c> and fans messages from per-session bounded channels into
    /// the same yielded stream, preserving FIFO ordering within each <c>SessionId</c>.
    /// </para>
    /// <para>
    /// <b>Full PeekLock/settlement behaviour</b> is validated by integration tests (R2.5).
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointName);
        ArgumentNullException.ThrowIfNull(flowControl);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);

        if (_options.EnableSessions)
        {
            await foreach (InboundMessage message in ConsumeSessionsAsync(endpointName, flowControl, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return message;
            }
        }
        else
        {
            await foreach (InboundMessage message in ConsumeNonSessionAsync(endpointName, flowControl, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return message;
            }
        }
    }

    // ── Non-session (R2.1) path ───────────────────────────────────────────────

    private async IAsyncEnumerable<InboundMessage> ConsumeNonSessionAsync(
        string endpointName,
        FlowControlOptions flowControl,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var receiverOptions = new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            PrefetchCount = _options.PrefetchCount,
        };

        ServiceBusReceiver receiver = _client!.CreateReceiver(endpointName, receiverOptions);

        var inboundChannel = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(flowControl.InternalQueueCapacity)
            {
                FullMode = flowControl.FullMode,
                SingleWriter = true,
                SingleReader = false,
            });

        string consumerId = Guid.NewGuid().ToString("N");

        var consumer = new AzureServiceBusConsumer(
            receiver: receiver,
            channel: inboundChannel,
            registry: _consumerRegistry,
            consumerId: consumerId,
            endpointName: endpointName,
            logger: _logger);

        _consumerRegistry.Register(consumerId, consumer);
        consumer.StartLoop();

        LogConsumerRegistered(consumerId, endpointName);

        try
        {
            await foreach (InboundMessage message in inboundChannel.Reader
                .ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            await consumer.StopAsync().ConfigureAwait(false);
            await consumer.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Session (R2.2) path ───────────────────────────────────────────────────

    private async IAsyncEnumerable<InboundMessage> ConsumeSessionsAsync(
        string endpointName,
        FlowControlOptions flowControl,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Shared output channel: per-session channels forward into this via the drain task.
        // SingleWriter = false because N drain tasks (one per active session) all write here.
        // FullMode is pinned to Wait — Drop* modes void per-session FIFO (D-9/PERF-1): a mid-session
        // message silently dropped here creates an ordering hole that the per-session channels above
        // already prevent, so both levels must agree on Wait to preserve the end-to-end guarantee.
        var outputChannel = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(flowControl.InternalQueueCapacity)
            {
                // Drop* modes void per-session FIFO (D-9/PERF-1), so the session path pins Wait.
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = false,
            });

        string consumerId = Guid.NewGuid().ToString("N");

        var sessionConsumer = new AzureServiceBusSessionConsumer(
            client: _client!,
            endpointName: endpointName,
            options: _options,
            registry: _consumerRegistry,
            consumerId: consumerId,
            outputChannel: outputChannel,
            flowControl: flowControl,
            logger: _logger);

        // Session consumers are tracked in _sessionConsumers, not in _consumerRegistry._consumers.
        // RegisterSession creates the message map and session index without touching AllConsumers().
        _consumerRegistry.RegisterSession(consumerId);

        lock (_sessionConsumersLock)
        {
            _sessionConsumers.Add(sessionConsumer);
        }

        sessionConsumer.StartLoop();

        LogSessionConsumerRegistered(consumerId, endpointName);

        try
        {
            await foreach (InboundMessage message in outputChannel.Reader
                .ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            await sessionConsumer.StopAsync().ConfigureAwait(false);
            await sessionConsumer.DisposeAsync().ConfigureAwait(false);

            lock (_sessionConsumersLock)
            {
                _sessionConsumers.Remove(sessionConsumer);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Recovers the <see cref="ServiceBusReceivedMessage"/> and <see cref="ServiceBusReceiver"/>
    /// from the registry via <c>message.DeliveryTag</c>, maps the <paramref name="action"/> via
    /// <see cref="AzureServiceBusSettlementRouter"/>, and calls the corresponding receiver method
    /// (<c>CompleteMessageAsync</c>, <c>AbandonMessageAsync</c>, <c>DeadLetterMessageAsync</c>, or
    /// <c>DeferMessageAsync</c>). For session messages, the registry returns the
    /// <see cref="ServiceBusSessionReceiver"/> (which inherits from <see cref="ServiceBusReceiver"/>),
    /// so settlement works polymorphically without a separate code path (D-3/OQ-1).
    /// </remarks>
    public async Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Resolve the consumer id from the stamped BW-ConsumerId header.
        if (!message.Headers.TryGetValue("BW-ConsumerId", out string? consumerId) ||
            string.IsNullOrEmpty(consumerId))
        {
            throw new BareWireTransportException(
                message: "Cannot settle message: BW-ConsumerId header is missing. " +
                         "The message was not delivered by an AzureServiceBusConsumer managed by this adapter.",
                transportName: TransportName,
                endpointAddress: null);
        }

        // Evict the delivery-tag entry exactly once (no unbounded buffers).
        (ServiceBusReceivedMessage Message, ServiceBusReceiver Receiver)? entry =
            _consumerRegistry.TryEvictMessage(consumerId, message.DeliveryTag);

        if (entry is null)
        {
            throw new BareWireTransportException(
                message: $"Cannot settle message: no registry entry found for DeliveryTag={message.DeliveryTag}, " +
                         $"consumer='{consumerId}'. The message may already have been settled or the consumer stopped.",
                transportName: TransportName,
                endpointAddress: null);
        }

        AzureServiceBusSettlementOperation operation = AzureServiceBusSettlementRouter.Map(action);

        try
        {
            switch (operation)
            {
                case AzureServiceBusSettlementOperation.Complete:
                    await entry.Value.Receiver
                        .CompleteMessageAsync(entry.Value.Message, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case AzureServiceBusSettlementOperation.Abandon:
                    await entry.Value.Receiver
                        .AbandonMessageAsync(entry.Value.Message, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case AzureServiceBusSettlementOperation.DeadLetter:
                    await entry.Value.Receiver
                        .DeadLetterMessageAsync(entry.Value.Message, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case AzureServiceBusSettlementOperation.Defer:
                    await entry.Value.Receiver
                        .DeferMessageAsync(entry.Value.Message, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown AzureServiceBusSettlementOperation value: {operation}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BareWireTransportException and not InvalidOperationException)
        {
            throw new BareWireTransportException(
                message: $"Failed to settle message (action={action}, DeliveryTag={message.DeliveryTag}) " +
                         $"on Azure Service Bus.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus consumer {ConsumerId} registered for queue '{QueueName}'.")]
    private partial void LogConsumerRegistered(string consumerId, string queueName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus session consumer {ConsumerId} registered for queue '{QueueName}'.")]
    private partial void LogSessionConsumerRegistered(string consumerId, string queueName);
}
