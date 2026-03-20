using System.Buffers;
using System.Threading.Channels;
using BareWire.Abstractions.Transport;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BareWire.Transport.RabbitMQ.Internal;

/// <summary>
/// Bridges RabbitMQ deliveries into a bounded <see cref="Channel{T}"/> of <see cref="InboundMessage"/>.
/// Inherits from <see cref="AsyncDefaultBasicConsumer"/> which provides default no-op implementations
/// for all <see cref="IAsyncBasicConsumer"/> methods.
/// </summary>
internal sealed class RabbitMqConsumer : AsyncDefaultBasicConsumer
{
    private readonly Channel<InboundMessage> _inboundChannel;
    private readonly RabbitMqHeaderMapper _headerMapper;
    private readonly string _consumerChannelId;

    internal RabbitMqConsumer(
        IChannel channel,
        Channel<InboundMessage> inboundChannel,
        RabbitMqHeaderMapper headerMapper,
        string consumerChannelId)
        : base(channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(inboundChannel);
        ArgumentNullException.ThrowIfNull(headerMapper);
        ArgumentException.ThrowIfNullOrEmpty(consumerChannelId);

        _inboundChannel = inboundChannel;
        _headerMapper = headerMapper;
        _consumerChannelId = consumerChannelId;
    }

    /// <inheritdoc />
    public override async Task HandleBasicDeliverAsync(
        string consumerTag,
        ulong deliveryTag,
        bool redelivered,
        string exchange,
        string routingKey,
        IReadOnlyBasicProperties properties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        // CRITICAL: RabbitMQ.Client frees the body memory after this handler returns.
        // We MUST copy the bytes before writing to the channel so consumers receive stable data.
        // Rent from ArrayPool to avoid per-message heap allocation (ADR-003 zero-copy).
        byte[]? pooledBuffer = null;
        ReadOnlySequence<byte> bodySequence;
        if (body.Length == 0)
        {
            bodySequence = ReadOnlySequence<byte>.Empty;
        }
        else
        {
            pooledBuffer = ArrayPool<byte>.Shared.Rent(body.Length);
            body.Span.CopyTo(pooledBuffer);
            bodySequence = new ReadOnlySequence<byte>(pooledBuffer, 0, body.Length);
        }

        Dictionary<string, string> headers = _headerMapper.MapInbound(properties);

        // Add endpoint routing information so SettleAsync can resolve the channel.
        // These are internal BareWire headers — added AFTER the mapper so they are not subject
        // to any custom mapping or passthrough filtering.
        headers["BW-RoutingKey"] = routingKey;
        headers["BW-Exchange"] = exchange;
        headers["BW-ConsumerChannelId"] = _consumerChannelId;

        string messageId = headers.TryGetValue("message-id", out string? mappedId) && !string.IsNullOrEmpty(mappedId)
            ? mappedId
            : Guid.NewGuid().ToString();

        InboundMessage message = new(
            messageId: messageId,
            headers: headers,
            body: bodySequence,
            deliveryTag: deliveryTag,
            pooledBuffer: pooledBuffer);

        // TryWrite returns false when the writer is completed (e.g. the consumer loop has ended).
        // In that case, nack-requeue the message so the broker can redeliver it to another consumer.
        // The pooled buffer must also be returned here since ReceiveEndpointRunner will never see this message.
        if (!_inboundChannel.Writer.TryWrite(message))
        {
            if (pooledBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(pooledBuffer);
            }

            await Channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override Task HandleChannelShutdownAsync(object channel, ShutdownEventArgs reason)
    {
        _inboundChannel.Writer.TryComplete();
        return Task.CompletedTask;
    }
}
