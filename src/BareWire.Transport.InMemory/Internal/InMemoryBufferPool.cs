using System.Buffers;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The single choke point every rent and return the in-memory transport performs on its own behalf —
/// settlement copies (<see cref="InMemoryBodyCopier"/>, dead-letter fan-out) and the consume runner's
/// requeue-on-abandon copies — goes through. Wraps <see cref="ArrayPool{T}.Shared"/> and notifies an
/// optional <see cref="IInMemoryBufferPoolObserver"/>, a test hook that is <see langword="null"/> in
/// production. One instance per <see cref="InMemoryTransportAdapter"/>, never a process-wide static pool.
/// </summary>
internal sealed class InMemoryBufferPool
{
    private readonly IInMemoryBufferPoolObserver? _observer;

    /// <param name="observer">
    /// An optional observer notified of every rent and return. Defaults to <see langword="null"/> — the
    /// production configuration, at the cost of a single extra null check per call.
    /// </param>
    internal InMemoryBufferPool(IInMemoryBufferPoolObserver? observer = null) => _observer = observer;

    /// <summary>
    /// Rents at least <c>Math.Max(length, 1)</c> bytes from <see cref="ArrayPool{T}.Shared"/> — a
    /// zero-length body still gets a private buffer rather than the shared empty array — and notifies the
    /// observer, when one is present.
    /// </summary>
    /// <param name="length">The number of bytes the caller needs at minimum. May be zero or negative.</param>
    internal byte[] Rent(int length)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        _observer?.OnRented(buffer);
        return buffer;
    }

    /// <summary>
    /// Notifies the observer, when one is present, then returns <paramref name="buffer"/> to
    /// <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    /// <param name="buffer">The buffer to return. Must have been rented from this pool.</param>
    internal void Return(byte[] buffer)
    {
        _observer?.OnReturned(buffer);
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
