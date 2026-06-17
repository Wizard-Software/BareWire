using Azure.Messaging.ServiceBus;
using BareWire.Abstractions.Transport;

namespace BareWire.Transport.AzureServiceBus;

// Partial that implements INativeMessageScheduler — native broker-level scheduled delivery
// (ScheduleMessageAsync / CancelScheduledMessageAsync). Implemented in R2.3.
internal sealed partial class AzureServiceBusTransportAdapter : INativeMessageScheduler
{
    /// <inheritdoc />
    public async Task<ScheduledMessageToken> ScheduleAsync(
        OutboundMessage message,
        DateTimeOffset scheduledEnqueueTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);

        ServiceBusSender sender = GetOrCreateSender(message.RoutingKey);

        // D-5: BinaryData.FromBytes(ReadOnlyMemory<byte>) wraps without copying — no ADR-003 deviation.
        var sb = new ServiceBusMessage(BinaryData.FromBytes(message.Body));

        AzureServiceBusHeaderMapper.MapOutbound(message.Headers, sb);

        if (!string.IsNullOrEmpty(message.ContentType))
        {
            sb.ContentType = message.ContentType;
        }

        long sequenceNumber = await sender
            .ScheduleMessageAsync(sb, scheduledEnqueueTime, cancellationToken)
            .ConfigureAwait(false);

        // Token carries the destination so CancelScheduledAsync can resolve the correct sender.
        return new ScheduledMessageToken(sequenceNumber, message.RoutingKey);
    }

    /// <inheritdoc />
    public async Task CancelScheduledAsync(
        ScheduledMessageToken token,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);

        // Resolve the sender from the destination carried by the token — no additional
        // state needed; the token is self-sufficient for cancel (D-GAP-1b).
        ServiceBusSender sender = GetOrCreateSender(token.Destination);

        await sender.CancelScheduledMessageAsync(token.SequenceNumber, cancellationToken)
            .ConfigureAwait(false);
    }
}
