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
    private const int StateLive = 0;
    private const int StateDisposed = 1;
    private const int StatePinned = 2;

    private byte[]? _pooledBuffer;

    // Lifetime of the pooled buffer, changed only with Interlocked: live, disposed (buffer returned or
    // about to be), or pinned by a transport that is copying the body while the consumer may dispose.
    private int _state;

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
    /// Internal — visible only to BareWire and test assemblies via InternalsVisibleTo.
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
    /// Pins the body so it stays readable until <see cref="UnpinPooledBuffer"/> is called, even if the
    /// consumer disposes the message in the meantime.
    /// </summary>
    /// <remarks>
    /// Decided by the same state as <see cref="Dispose"/>: pinning fails once the message has been
    /// disposed. A <see cref="Dispose"/> that arrives while the body is pinned does not return the buffer;
    /// <see cref="UnpinPooledBuffer"/> does, so the buffer goes back to the pool exactly once and never
    /// while it is being read. At most one caller may hold a pin.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when the body is pinned and may be read; <see langword="false"/> when the
    /// message was already disposed or pinned.
    /// </returns>
    internal bool TryPinPooledBuffer() =>
        Interlocked.CompareExchange(ref _state, StatePinned, StateLive) == StateLive;

    /// <summary>
    /// Releases a pin taken with <see cref="TryPinPooledBuffer"/>. When the message was disposed while
    /// pinned, the pooled buffer is returned to the pool here instead.
    /// </summary>
    internal void UnpinPooledBuffer()
    {
        if (Interlocked.CompareExchange(ref _state, StateLive, StatePinned) == StatePinned)
        {
            return;
        }

        ReturnPooledBuffer();
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
        // A pinned buffer is returned by UnpinPooledBuffer once the pin holder has finished reading it.
        if (Interlocked.Exchange(ref _state, StateDisposed) != StateLive)
            return;

        ReturnPooledBuffer();
    }

    private void ReturnPooledBuffer()
    {
        if (_pooledBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_pooledBuffer);
            _pooledBuffer = null;
        }
    }
}
