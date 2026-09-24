namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Observes every rent and return the in-memory transport performs on its own behalf — through
/// <see cref="InMemoryBufferPool"/> — as opposed to a buffer handed to an
/// <see cref="BareWire.Abstractions.Transport.InboundMessage"/>, which the message itself returns to
/// <see cref="System.Buffers.ArrayPool{T}.Shared"/> on <see cref="System.IDisposable.Dispose"/> without
/// going through this pool.
/// </summary>
/// <remarks>
/// A test hook. Production code never supplies an implementation — <see cref="InMemoryBufferPool"/>
/// accepts <see langword="null"/> and pays exactly one null check per rent or return when none is
/// supplied.
/// </remarks>
internal interface IInMemoryBufferPoolObserver
{
    /// <summary>Called immediately after <paramref name="buffer"/> was rented from the shared pool.</summary>
    /// <param name="buffer">The rented buffer.</param>
    void OnRented(byte[] buffer);

    /// <summary>Called immediately before <paramref name="buffer"/> is returned to the shared pool.</summary>
    /// <param name="buffer">The buffer about to be returned.</param>
    void OnReturned(byte[] buffer);
}
