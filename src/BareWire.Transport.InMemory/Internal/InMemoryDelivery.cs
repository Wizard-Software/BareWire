namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// A single delivery held in an <see cref="InMemoryQueue"/>'s bounded channel: a byte buffer rented
/// from <see cref="System.Buffers.ArrayPool{T}"/> by the writer, together with the number of valid
/// bytes at the start of that buffer.
/// </summary>
internal sealed class InMemoryDelivery
{
    /// <param name="buffer">The rented buffer. Must not be <see langword="null"/>.</param>
    /// <param name="length">
    /// The number of valid bytes at the start of <paramref name="buffer"/>. Must be between zero and
    /// <paramref name="buffer"/>'s length, inclusive.
    /// </param>
    internal InMemoryDelivery(byte[] buffer, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, buffer.Length);

        Buffer = buffer;
        Length = length;
    }

    /// <summary>
    /// Gets the underlying buffer rented from the shared array pool by the writer. This is exposed for
    /// the buffer's owner (to return it to the pool once the delivery is settled) — readers of the
    /// delivery's content should use <see cref="Body"/> instead, which is trimmed to the valid range.
    /// </summary>
    internal byte[] Buffer { get; }

    /// <summary>Gets the number of valid bytes at the start of <see cref="Buffer"/>.</summary>
    internal int Length { get; }

    /// <summary>Gets the valid portion of <see cref="Buffer"/> as a read-only view.</summary>
    internal ReadOnlyMemory<byte> Body => Buffer.AsMemory(0, Length);
}
