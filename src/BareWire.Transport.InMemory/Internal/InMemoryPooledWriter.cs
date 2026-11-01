using System.Buffers;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> backed by a buffer rented from <see cref="ArrayPool{T}.Shared"/>.
/// Grows by doubling (copying into a larger rented buffer and returning the old one); <see cref="Dispose"/>
/// returns the current buffer exactly once. Not thread-safe.
/// </summary>
internal sealed class InMemoryPooledWriter : IBufferWriter<byte>, IDisposable
{
    private byte[]? _buffer;
    private int _written;

    /// <param name="initialCapacity">The minimum size of the first rented buffer. Must be positive.</param>
    internal InMemoryPooledWriter(int initialCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    /// <summary>Gets the bytes written so far; valid until the next write or <see cref="Dispose"/>.</summary>
    internal ReadOnlyMemory<byte> WrittenMemory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            return _buffer.AsMemory(0, _written);
        }
    }

    /// <inheritdoc />
    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length - _written)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Cannot advance past the end of the buffer.");
        }

        _written += count;
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    /// <summary>Returns the rented buffer to the pool. Idempotent.</summary>
    public void Dispose()
    {
        byte[]? buffer = _buffer;
        if (buffer is null)
        {
            return;
        }

        _buffer = null;
        _written = 0;
        ArrayPool<byte>.Shared.Return(buffer);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        int needed = Math.Max(sizeHint, 1);
        if (_buffer.Length - _written >= needed)
        {
            return;
        }

        long target = Math.Max((long)_buffer.Length * 2, (long)_written + needed);
        int newSize = (int)Math.Min(target, Array.MaxLength);
        if (newSize - _written < needed)
        {
            throw new InvalidOperationException("The pooled writer cannot grow to the requested size.");
        }

        byte[] larger = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _written).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }
}
