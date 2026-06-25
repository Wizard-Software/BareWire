using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace BareWire.Transport.RabbitMQ;

internal sealed partial class RabbitMqTransportAdapter
{
    /// <inheritdoc cref="IDurableParkSettlement.ParkHeadDurablyAsync"/>
    public async Task<DurableSettlementResult> ParkHeadDurablyAsync(
        InboundMessage message,
        string deadLetterExchange,
        string deadLetterRoutingKey,
        CancellationToken cancellationToken = default)
    {
        // Resolve the consumer channel BEFORE creating the confirm channel.
        // If the channel cannot be found the head stays unacknowledged (C3 invariant).
        IChannel? consumerChannel = ResolveChannelForMessage(message);
        if (consumerChannel is null)
        {
            return new DurableSettlementResult(false, "consumer channel not found");
        }

        // Create an ephemeral publisher-confirm–enabled channel for the re-publication.
        // With PublisherConfirmationTrackingEnabled=true the client correlates basic.return
        // responses (mandatory=true, no bound queue) with the publish sequence number and
        // surfaces them as PublishException (IsReturn=true). Catching PublishException
        // therefore handles both broker-nack and unroutable-mandatory paths (PERF-3).
        IChannel confirmChannel;
        try
        {
            var channelOptions = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true);

            confirmChannel = await _connection!
                .CreateChannelAsync(channelOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Channel-max exhausted or connection-level failure — failed-settle, not crash.
            return new DurableSettlementResult(false, "channel unavailable");
        }

        try
        {
            (BasicProperties props, Dictionary<string, object?> amqpHeaders) =
                _headerMapper.MapOutbound(message.Headers);

            if (amqpHeaders.Count > 0)
            {
                props.Headers = amqpHeaders;
            }

            // Flatten ReadOnlySequence<byte> → ReadOnlyMemory<byte>.
            // Single-segment fast-path is zero-copy; multi-segment allocates (park is not hot-path).
            ReadOnlyMemory<byte> body = FlattenBody(message.Body);

            // mandatory:true ensures the broker rejects (via basic.return surfaced as
            // PublishException.IsReturn=true) if the dead-letter exchange has no bound queue,
            // preventing a false durable-ack that would break C3.
            await confirmChannel.BasicPublishAsync<BasicProperties>(
                exchange: deadLetterExchange,
                routingKey: deadLetterRoutingKey,
                mandatory: true,
                basicProperties: props,
                body: body,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (PublishException ex) when (ex.IsReturn)
        {
            // Broker returned the message — dead-letter exchange has no bound queue.
            // SEC-2: log only the exchange name (config constant), never body/headers.
            LogDurableParkMessageReturned(deadLetterExchange);
            return new DurableSettlementResult(false, "message returned unrouted");
        }
        catch (PublishException)
        {
            // Broker sent a basic.nack — message was not durably stored.
            // SEC-2: log only the exchange name, never body/headers.
            LogDurableParkNack(deadLetterExchange);
            return new DurableSettlementResult(false, "publish nack");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unexpected channel-level failure.
            LogDurableParkNack(deadLetterExchange);
            return new DurableSettlementResult(false, "publish nack");
        }
        finally
        {
            try
            {
                await confirmChannel.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogPublishChannelCloseError(ex);
            }

            await confirmChannel.DisposeAsync().ConfigureAwait(false);
        }

        // Re-publication was durably confirmed by the broker.
        // ACK the original delivery ONLY now — this is the C3 invariant:
        // the ordering key must not be released before the head is durably parked.
        await consumerChannel.BasicAckAsync(
            deliveryTag: message.DeliveryTag,
            multiple: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new DurableSettlementResult(true, null);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Durable park publish nack from broker for dead-letter exchange '{DeadLetterExchange}'.")]
    private partial void LogDurableParkNack(string deadLetterExchange);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Durable park message returned unrouted from dead-letter exchange '{DeadLetterExchange}'. " +
                  "No queue is bound to the exchange — settlement not confirmed.")]
    private partial void LogDurableParkMessageReturned(string deadLetterExchange);

    /// <summary>
    /// Flattens a <see cref="System.Buffers.ReadOnlySequence{T}"/> of bytes into a
    /// <see cref="ReadOnlyMemory{T}"/>.
    /// Single-segment sequences are returned zero-copy via <c>First</c>;
    /// multi-segment sequences are copied into a new array (park is not a hot-path).
    /// </summary>
    private static ReadOnlyMemory<byte> FlattenBody(System.Buffers.ReadOnlySequence<byte> sequence)
    {
        if (sequence.IsSingleSegment)
        {
            return sequence.First;
        }

        // Multi-segment: copy into a contiguous array. Allocation is acceptable for park (non-hot-path).
        byte[] buffer = new byte[sequence.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in sequence)
        {
            segment.Span.CopyTo(buffer.AsSpan(offset));
            offset += segment.Length;
        }

        return buffer;
    }
}
