namespace BareWire.Abstractions.Outbox;

/// <summary>
/// Controls the local dispatch ordering guarantee for outbox messages.
/// </summary>
/// <remarks>
/// The default mode <see cref="None"/> preserves pre-R7.7 behavior exactly —
/// no ordering guarantee and no additional overhead.
/// Room is left for future modes such as <c>StrictFifo</c> (global ordering, requiring a
/// distributed sequencer) if the need arises.
/// </remarks>
public enum OrderingMode
{
    /// <summary>
    /// No ordering guarantee is applied.
    /// Outbox rows are claimed and dispatched in arbitrary order.
    /// This is the default and produces behavior that is bit-identical to the pre-R7.7 baseline —
    /// no new index is created, no additional grouping is performed.
    /// </summary>
    None = 0,

    /// <summary>
    /// Head-of-line ordering is enforced per <c>OrderingKey</c>.
    /// Within a key group, only the oldest undelivered row may be claimed in each cycle;
    /// newer rows for the same key are held back until the head is confirmed.
    /// Rows without a key (keyless) are always eligible and pass through in parallel.
    /// </summary>
    PerKey = 1,
}
