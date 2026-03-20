using System.Buffers;
using System.Threading;

namespace BareWire.Abstractions.Transport;

/// <summary>
/// Represents a raw message received from the transport layer before deserialization.
/// Carries the zero-copy body as a <see cref="ReadOnlySequence{T}"/> of bytes along with
/// transport-level metadata.
/// </summary>
/// <remarks>
/// Implements <see cref="IDisposable"/> to return the underlying <see cref="ArrayPool{T}"/>-rented
/// buffer back to the pool when the message has been fully processed. The consumer pipeline owns the
/// lifetime of this instance and must call <see cref="Dispose"/> after settlement.
/// </remarks>
public sealed class InboundMessage : IDisposable
{
    private byte[]? _pooledBuffer;
    private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked)

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
    /// Internal — visible only to BareWire.Core and test assemblies via InternalsVisibleTo.
    /// </summary>
    internal byte[]? PooledBuffer => _pooledBuffer;

    /// <summary>
    /// Initializes a new instance of <see cref="InboundMessage"/> with all required transport metadata.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message. Must not be null.</param>
    /// <param name="headers">The transport-level headers. Must not be null.</param>
    /// <param name="body">The raw zero-copy body of the message.</param>
    /// <param name="deliveryTag">The transport-specific delivery tag for settlement.</param>
    /// <param name="pooledBuffer">
    /// Optional <see cref="ArrayPool{T}"/>-rented buffer backing <paramref name="body"/>.
    /// When non-null, ownership transfers to this instance — the transport must not return it.
    /// The buffer will be returned to <see cref="ArrayPool{T}.Shared"/> when <see cref="Dispose"/> is called.
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
        _pooledBuffer = pooledBuffer;
    }

    /// <summary>
    /// Returns the <see cref="ArrayPool{T}"/>-rented buffer (if any) back to
    /// <see cref="ArrayPool{T}.Shared"/>. Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    /// <remarks>
    /// After disposal, <see cref="Body"/> still refers to the returned memory segment.
    /// Accessing <see cref="Body"/> after <see cref="Dispose"/> is undefined behaviour — the
    /// caller must not read the body once the message has been settled and disposed.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_pooledBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_pooledBuffer);
            _pooledBuffer = null;
        }
    }
}
