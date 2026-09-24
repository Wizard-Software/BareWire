namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Notified by <see cref="InMemoryQueue"/> of the winning side of every latch transition — one
/// <see cref="OnLatchSet"/> call per episode, from the thread that opened it, and one matching
/// <see cref="OnLatchCleared"/> call, from the thread that closed it. Registered via
/// <see cref="InMemoryQueue.SetLatchObserver"/>; the most recently registered observer wins ("last
/// observer wins" — see that method's remarks).
/// </summary>
/// <remarks>
/// Both callbacks are invoked OUTSIDE any lock <see cref="InMemoryQueue"/> holds, and
/// <see cref="OnLatchCleared"/> is always invoked after the occupancy counter change that triggered the
/// clear has already landed (the freed slot is already visible to other callers). Neither callback may
/// ever throw — an implementation that fails internally must catch its own exceptions and count them
/// instead of letting them propagate into <see cref="InMemoryQueue"/>'s own call stack, which runs on
/// hot paths (including inside <c>ReleaseSlot</c>, on dispose paths, before a buffer is returned to its
/// pool).
/// </remarks>
internal interface IInMemoryQueueLatchObserver
{
    /// <summary>
    /// Returns a monotonic timestamp (in the same units as <see cref="TimeProvider.GetTimestamp"/>) used
    /// to seed <see cref="InMemoryLatchEpisode.StartTimestamp"/> when a new episode opens. Must not
    /// throw — <see cref="InMemoryQueue"/> guards every call to this member defensively regardless, and
    /// falls back to zero if it does.
    /// </summary>
    long GetTimestamp();

    /// <summary>
    /// Called once per latch episode, by the thread that opened it (the winner of the 0→1 latch
    /// transition, once its immediate re-check did not revert it). Must not throw.
    /// </summary>
    /// <param name="queue">The queue whose latch was just set.</param>
    /// <param name="episode">
    /// The episode's snapshot, taken at open time. The SAME instance is later passed to
    /// <see cref="OnLatchCleared"/> for the matching close.
    /// </param>
    void OnLatchSet(InMemoryQueue queue, InMemoryLatchEpisode episode);

    /// <summary>
    /// Called once per latch episode, by the thread that closed it (the winner of the 1→0 latch
    /// transition). Must not throw.
    /// </summary>
    /// <param name="queue">The queue whose latch was just cleared.</param>
    /// <param name="episode">The same episode instance passed to the matching <see cref="OnLatchSet"/> call.</param>
    void OnLatchCleared(InMemoryQueue queue, InMemoryLatchEpisode episode);
}

/// <summary>
/// A single latch episode's snapshot: taken under <see cref="InMemoryQueue"/>'s internal episode lock at
/// the moment the episode opens, and handed unchanged to both
/// <see cref="IInMemoryQueueLatchObserver.OnLatchSet"/> and <see cref="IInMemoryQueueLatchObserver.OnLatchCleared"/>.
/// Allocated only at a latch transition — never per message.
/// </summary>
internal sealed class InMemoryLatchEpisode
{
    /// <summary>
    /// The value <see cref="IInMemoryQueueLatchObserver.GetTimestamp"/> returned when this episode opened.
    /// </summary>
    internal required long StartTimestamp { get; init; }

    /// <summary>
    /// <see cref="InMemoryQueue.RejectedCopyCount"/> at the moment this episode opened — the baseline an
    /// observer subtracts from the count at close time to report how many copies were rejected during
    /// this episode specifically.
    /// </summary>
    internal required long RejectedCopiesAtStart { get; init; }

    /// <summary>
    /// Free-form state an observer implementation may use to coordinate its own open/close handling of
    /// this episode (for example, a race-safe hand-off between the two calls). Owned entirely by the
    /// observer — <see cref="InMemoryQueue"/> never reads or writes it.
    /// </summary>
    internal int ObserverState;
}
