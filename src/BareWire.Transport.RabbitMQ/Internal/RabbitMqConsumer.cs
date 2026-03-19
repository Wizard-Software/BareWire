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

    internal RabbitMqConsumer(
        IChannel channel,
        Channel<InboundMessage> inboundChannel,
        RabbitMqHeaderMapper headerMapper)
        : base(channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(inboundChannel);
        ArgumentNullException.ThrowIfNull(headerMapper);

        _inboundChannel = inboundChannel;
        _headerMapper = headerMapper;
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
        byte[] bodyCopy = body.ToArray();
        ReadOnlySequence<byte> bodySequence = bodyCopy.Length == 0
            ? ReadOnlySequence<byte>.Empty
            : new ReadOnlySequence<byte>(bodyCopy);

        Dictionary<string, string> headers = _headerMapper.MapInbound(properties);

        // Add endpoint routing information so SettleAsync can resolve the channel.
        // These are internal BareWire headers — added AFTER the mapper so they are not subject
        // to any custom mapping or passthrough filtering.
        headers["BW-RoutingKey"] = routingKey;
        headers["BW-Exchange"] = exchange;

        string messageId = headers.TryGetValue("message-id", out string? mappedId) && !string.IsNullOrEmpty(mappedId)
            ? mappedId
            : Guid.NewGuid().ToString();

        InboundMessage message = new(
            messageId: messageId,
            headers: headers,
            body: bodySequence,
            deliveryTag: deliveryTag);

        await _inboundChannel.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task HandleChannelShutdownAsync(object channel, ShutdownEventArgs reason)
    {
        _inboundChannel.Writer.TryComplete();
        return Task.CompletedTask;
    }
}
