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
/// <see cref="ITransportAdapter.SettleAsync"/> (R2.1).
/// </summary>
internal sealed partial class AzureServiceBusTransportAdapter
{
    private readonly AzureServiceBusConsumerRegistry _consumerRegistry = new();

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Builds a <see cref="ServiceBusReceiver"/> in PeekLock mode (D-1), starts an
    /// <see cref="AzureServiceBusConsumer"/> polling loop, and exposes received messages as
    /// an <see cref="IAsyncEnumerable{T}"/> via a bounded <see cref="Channel{T}"/> (ADR-004).
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

    /// <inheritdoc />
    /// <remarks>
    /// Recovers the <see cref="ServiceBusReceivedMessage"/> and <see cref="ServiceBusReceiver"/>
    /// from the registry via <c>message.DeliveryTag</c>, maps the <paramref name="action"/> via
    /// <see cref="AzureServiceBusSettlementRouter"/>, and calls the corresponding receiver method
    /// (<c>CompleteMessageAsync</c>, <c>AbandonMessageAsync</c>, <c>DeadLetterMessageAsync</c>, or
    /// <c>DeferMessageAsync</c>).
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
        catch (Exception ex) when (ex is not OperationCanceledException and not BareWireTransportException)
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
}
