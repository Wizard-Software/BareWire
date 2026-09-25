namespace BareWire.Transport.InMemory.Internal;

/// <summary>The outcome of an <see cref="InMemoryQueue.TryReserve"/> call.</summary>
internal enum QueueReservationResult
{
    /// <summary>
    /// A slot was reserved. The caller MUST follow with exactly one
    /// <see cref="InMemoryQueue.WriteReserved"/> (or release the slot with
    /// <see cref="InMemoryQueue.ReleaseSlot"/> if it decides not to write after all).
    /// </summary>
    Reserved,

    /// <summary>
    /// The queue is full but not latched, and it has at least one active consumer. The caller may wait
    /// once for a slot to become available.
    /// </summary>
    Full,

    /// <summary>
    /// The latch is set (whether it was set by this call or an earlier one). The caller must reject
    /// immediately and never wait.
    /// </summary>
    Latched,
}
