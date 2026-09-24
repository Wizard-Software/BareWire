using System.Collections.Concurrent;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Schedules a deferred redelivery — a copy of a settled delivery, written back to its own queue's
/// already-reserved slot after a delay — for the in-memory transport's opt-in <c>Defer</c> settlement
/// action. One instance per <see cref="InMemoryTransportAdapter"/>, created only when
/// <see cref="InMemoryTransportOptions.DeferEnabled"/> is on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership transfer.</b> A successful <see cref="TrySchedule"/> call transfers ownership of
/// <c>redelivery</c> (and the queue slot it already holds) to this scheduler: from that point on, exactly
/// one of three paths retires it — the timer callback (writes it, or drops it if this scheduler was
/// disposed in the meantime), or <see cref="Dispose"/>'s own sweep (drops every entry still pending). A
/// <c>TryRemove</c> on the same key of the internal pending-entry dictionary makes whichever of those two
/// runs first the single winner; the loser does nothing further for that entry. <see cref="TrySchedule"/>
/// only returns <see langword="false"/> — leaving the queue slot and the copy to the CALLER to release and
/// return — when this scheduler was already disposed before it created any entry at all, so nothing here
/// could possibly claim it later.
/// </para>
/// <para>
/// <b>Timer ordering.</b> Each entry's <see cref="ITimer"/> is created with an infinite due time FIRST, the
/// entry is added to the pending map SECOND, and only THEN is the timer armed with the real delay — so the
/// callback can never fire before the entry it looks up actually exists. A re-check of the disposed flag
/// immediately after the add (and before arming) closes the one remaining race: a concurrent
/// <see cref="Dispose"/> that already took its cleanup sweep before this entry existed would otherwise
/// never come back for it.
/// </para>
/// <para>
/// <b>The callback never throws.</b> The only expected failure is the target queue's channel rejecting the
/// write (an invariant violation, since the redelivery already holds a reserved slot) —
/// <see cref="InvalidOperationException"/> from <see cref="InMemoryQueue.WriteReserved"/> is caught
/// explicitly, logged at <see cref="Microsoft.Extensions.Logging.LogLevel.Error"/>, and the delivery is
/// dropped (slot released, buffer returned) instead of redelivered. No other step in the callback is
/// expected to throw.
/// </para>
/// </remarks>
internal sealed class InMemoryDeferScheduler : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly InMemoryBufferPool _pool;
    private readonly InMemoryConsumeDiagnostics _diagnostics;
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private long _nextId;
    private int _disposed;

    /// <param name="timeProvider">The time source the deferral timers are created from.</param>
    /// <param name="pool">The pool a dropped redelivery's buffer is returned to.</param>
    /// <param name="diagnostics">Reports deliveries dropped on shutdown and deferred-redelivery failures.</param>
    internal InMemoryDeferScheduler(TimeProvider timeProvider, InMemoryBufferPool pool, InMemoryConsumeDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _timeProvider = timeProvider;
        _pool = pool;
        _diagnostics = diagnostics;
    }

    /// <summary>Gets the number of deferred redeliveries currently pending. A test hook.</summary>
    internal int PendingCount => _pending.Count;

    /// <summary>
    /// Schedules <paramref name="redelivery"/> to be written back to <paramref name="queue"/> — which
    /// <paramref name="redelivery"/> already holds a reserved slot on — after <paramref name="delay"/>.
    /// </summary>
    /// <param name="queue">The queue <paramref name="redelivery"/> will be written back to.</param>
    /// <param name="redelivery">
    /// The redelivery to write back. MUST already hold a reserved slot on <paramref name="queue"/> (the
    /// same slot the original delivery held).
    /// </param>
    /// <param name="delay">The delay before the redelivery is written back. Must not be negative.</param>
    /// <returns>
    /// <see langword="true"/> when ownership of <paramref name="redelivery"/> was transferred to this
    /// scheduler — see this type's remarks; the caller must not touch the queue slot or the buffer again.
    /// <see langword="false"/> when this scheduler was already disposed and never took ownership at all —
    /// the caller must release the slot and return the buffer itself.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="queue"/> or <paramref name="redelivery"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay"/> is negative.</exception>
    internal bool TrySchedule(InMemoryQueue queue, InMemoryDelivery redelivery, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(redelivery);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        // Nothing has been created yet: the caller still owns the slot and the buffer and must clean up.
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        long id = Interlocked.Increment(ref _nextId);
        var pending = new Pending(queue, redelivery);

        // Infinite due time first — see this type's remarks on timer ordering.
        pending.Timer = _timeProvider.CreateTimer(TimerCallback, id, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _pending[id] = pending;

        if (Volatile.Read(ref _disposed) != 0)
        {
            if (_pending.TryRemove(id, out _))
            {
                // We won the race: Dispose()'s own sweep either already finished or never started looking
                // for this id — nobody else will ever clean this entry up, so we must, right here.
                pending.Timer.Dispose();
                ReleaseAndReturn(pending);
                _diagnostics.DeliveriesDroppedOnShutdown(pending.Queue.Name, 1);
            }

            // Otherwise Dispose()'s sweep already claimed and fully cleaned up this entry. Either way,
            // ownership was successfully transferred away from the caller.
            return true;
        }

        pending.Timer.Change(delay, Timeout.InfiniteTimeSpan);
        return true;
    }

    private void TimerCallback(object? state)
    {
        long id = (long)state!;
        if (!_pending.TryRemove(id, out Pending? pending))
        {
            // Already claimed by Dispose()'s sweep.
            return;
        }

        pending.Timer!.Dispose();

        if (Volatile.Read(ref _disposed) != 0)
        {
            ReleaseAndReturn(pending);
            _diagnostics.DeliveriesDroppedOnShutdown(pending.Queue.Name, 1);
            return;
        }

        bool written = false;
        try
        {
            pending.Queue.WriteReserved(pending.Redelivery);
            written = true;
        }
        catch (InvalidOperationException ex)
        {
            _diagnostics.DeferredRedeliveryFailed(pending.Queue.Name, ex);
        }
        finally
        {
            if (!written)
            {
                ReleaseAndReturn(pending);
            }
        }
    }

    /// <summary>
    /// Claims every entry still pending, disposes its timer, releases its queue slot, returns its buffer,
    /// and reports one aggregated <c>DeliveriesDroppedOnShutdown</c> count per queue. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_pending.IsEmpty)
        {
            return;
        }

        var droppedPerQueue = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<long, Pending> entry in _pending)
        {
            if (!_pending.TryRemove(entry.Key, out Pending? pending))
            {
                // Already claimed by the timer callback, or by a concurrent TrySchedule's own recheck.
                continue;
            }

            pending.Timer!.Dispose();
            ReleaseAndReturn(pending);
            droppedPerQueue[pending.Queue.Name] = droppedPerQueue.GetValueOrDefault(pending.Queue.Name) + 1;
        }

        foreach (KeyValuePair<string, int> dropped in droppedPerQueue)
        {
            _diagnostics.DeliveriesDroppedOnShutdown(dropped.Key, dropped.Value);
        }
    }

    private void ReleaseAndReturn(Pending pending)
    {
        pending.Queue.ReleaseSlot();
        _pool.Return(pending.Redelivery.Buffer);
    }

    private sealed class Pending(InMemoryQueue queue, InMemoryDelivery redelivery)
    {
        internal InMemoryQueue Queue { get; } = queue;

        internal InMemoryDelivery Redelivery { get; } = redelivery;

        internal ITimer? Timer { get; set; }
    }
}
