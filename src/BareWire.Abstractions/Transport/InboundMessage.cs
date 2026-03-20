using System.Buffers;

namespace BareWire.Abstractions.Transport;

/// <summary>
/// Represents a raw message received from the transport layer before deserialization.
/// Carries the zero-copy body as a <see cref="ReadOnlySequence{T}"/> of bytes along with
/// transport-level metadata.
/// </summary>
public sealed class InboundMessage
{
    /// <summary>
    /// Gets the unique identifier of the message assigned by the transport or originating publisher.
    /// </summary>
    public string MessageId { get; }

    /// <summary>
    /// Gets the transport-level headers associated with the message.
    /// Never null — an empty dictionary is returned when no headers are present.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// Gets the raw body of the message as a zero-copy <see cref="ReadOnlySequence{T}"/>.
    /// The sequence is valid only for the lifetime of the consume callback; it must not be retained.
    /// </summary>
    public ReadOnlySequence<byte> Body { get; }

    /// <summary>
    /// Gets the transport-specific delivery tag used to acknowledge or settle the message.
    /// </summary>
    public ulong DeliveryTag { get; }

    /// <summary>
    /// Gets the <see cref="ArrayPool{T}"/>-rented buffer backing <see cref="Body"/>, or
    /// <see langword="null"/> when the body is empty or backed by non-pooled memory.
    /// Must be returned to <see cref="ArrayPool{T}.Shared"/> after the message is settled.
    /// </summary>
    public byte[]? PooledBuffer { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="InboundMessage"/> with all required transport metadata.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message. Must not be null.</param>
    /// <param name="headers">The transport-level headers. Must not be null.</param>
    /// <param name="body">The raw zero-copy body of the message.</param>
    /// <param name="deliveryTag">The transport-specific delivery tag for settlement.</param>
    /// <param name="pooledBuffer">
    /// Optional <see cref="ArrayPool{T}"/>-rented buffer backing <paramref name="body"/>.
    /// When non-null, ownership transfers to the message pipeline — the transport must not return it.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="messageId"/> or <paramref name="headers"/> is null.
    /// </exception>
    public InboundMessage(
        string messageId,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlySequence<byte> body,
        ulong deliveryTag,
        byte[]? pooledBuffer = null)
    {
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        Headers = headers ?? throw new ArgumentNullException(nameof(headers));
        Body = body;
        DeliveryTag = deliveryTag;
        PooledBuffer = pooledBuffer;
    }
}
