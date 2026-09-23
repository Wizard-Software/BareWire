namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// A single delivery held in an <see cref="InMemoryQueue"/>'s bounded channel: a byte buffer rented
/// from <see cref="System.Buffers.ArrayPool{T}"/> by the writer, together with the number of valid
/// bytes at the start of that buffer, the header set stamped for the publish this delivery belongs
/// to, and the number of times this delivery has already been redelivered.
/// </summary>
internal sealed class InMemoryDelivery
{
    private readonly IReadOnlyDictionary<string, string> _inboundHeaders;

    /// <param name="buffer">The rented buffer. Must not be <see langword="null"/>.</param>
    /// <param name="length">
    /// The number of valid bytes at the start of <paramref name="buffer"/>. Must be between zero and
    /// <paramref name="buffer"/>'s length, inclusive.
    /// </param>
    /// <param name="headers">
    /// The header set stamped for the publish this delivery belongs to — the same instance is shared,
    /// unchanged, by every fan-out copy of that publish. Defaults to <see cref="InMemoryHeaderSet.Empty"/>.
    /// </param>
    /// <param name="redeliveryCount">
    /// The number of times this delivery has already been redelivered; zero on first delivery. Must
    /// not be negative.
    /// </param>
    internal InMemoryDelivery(byte[] buffer, int length, InMemoryHeaderSet? headers = null, int redeliveryCount = 0)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, buffer.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(redeliveryCount);

        Buffer = buffer;
        Length = length;
        Headers = headers ?? InMemoryHeaderSet.Empty;
        RedeliveryCount = redeliveryCount;
        _inboundHeaders = redeliveryCount > 0 ? new RedeliveryHeaderOverlay(Headers, redeliveryCount) : Headers;
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

    /// <summary>
    /// Gets the header set stamped for the publish this delivery belongs to. The same instance is
    /// shared, by reference, by every fan-out copy of that publish — it is never copied or mutated per
    /// copy, and its <see cref="InMemoryHeaderSet.MessageId"/> is this delivery's <see cref="MessageId"/>.
    /// </summary>
    internal InMemoryHeaderSet Headers { get; }

    /// <summary>
    /// Gets the number of times this delivery has already been redelivered; zero on first delivery.
    /// This is the authoritative count and is never read back from a header — the publisher's own
    /// redelivery-count header, in any casing, is stripped by <see cref="InMemoryHeaderSet.Stamp"/>.
    /// </summary>
    internal int RedeliveryCount { get; }

    /// <summary>Gets the message identifier resolved for the publish this delivery belongs to.</summary>
    internal string MessageId => Headers.MessageId;

    /// <summary>
    /// Gets the headers a consumer should see for this delivery: <see cref="Headers"/> unchanged on
    /// first delivery, or a <see cref="RedeliveryHeaderOverlay"/> materializing the redelivery-count
    /// header when <see cref="RedeliveryCount"/> is greater than zero. Built exactly once, in the
    /// constructor, and held in a field — reading this property never allocates and always returns the
    /// same instance for a given delivery.
    /// </summary>
    internal IReadOnlyDictionary<string, string> InboundHeaders => _inboundHeaders;

    /// <summary>
    /// Creates the next redelivery of this delivery: the same <see cref="Headers"/> instance (so
    /// <see cref="MessageId"/> stays stable across redeliveries), with <see cref="RedeliveryCount"/>
    /// incremented by one. This delivery is never mutated.
    /// </summary>
    /// <param name="buffer">The rented buffer for the redelivery. Must not be <see langword="null"/>.</param>
    /// <param name="length">The number of valid bytes at the start of <paramref name="buffer"/>.</param>
    internal InMemoryDelivery CreateRedelivery(byte[] buffer, int length) =>
        new(buffer, length, Headers, RedeliveryCount + 1);
}
