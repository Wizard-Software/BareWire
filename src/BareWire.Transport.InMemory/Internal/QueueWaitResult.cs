namespace BareWire.Transport.InMemory.Internal;

/// <summary>The outcome of an <see cref="InMemoryQueue.WaitToReserveAsync"/> call.</summary>
internal enum QueueWaitResult
{
    /// <summary>
    /// A slot was reserved before the deadline elapsed. The caller MUST follow with exactly one
    /// <see cref="InMemoryQueue.WriteReserved"/>.
    /// </summary>
    Reserved,

    /// <summary>No slot became available before the requested timeout elapsed.</summary>
    TimedOut,

    /// <summary>The wait was cancelled through the supplied <see cref="CancellationToken"/>.</summary>
    Cancelled,

    /// <summary>The latch was observed set (now or earlier). The caller must reject and never retry.</summary>
    Latched,
}
