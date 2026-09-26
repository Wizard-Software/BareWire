using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// A single in-memory transport queue: an admission counter independent of the bounded channel it
/// backs, a bounded channel written to exclusively after a successful reservation, a "full" latch with
/// 50% release hysteresis, and the operational definition of an "active consumer" used to decide
/// whether a full queue may be waited on or must latch immediately.
/// </summary>
/// <remarks>
/// <para>
/// <b>Occupancy.</b> <see cref="Occupancy"/> counts everything that belongs to the queue: messages
/// sitting in the channel, messages handed to a reader but not yet settled, and messages requeued or
/// held outside the channel pending a deferred redelivery. It increases only in <see cref="TryReserve"/>
/// (and its waiting counterpart, <see cref="WaitToReserveAsync"/>) and decreases only in
/// <see cref="ReleaseSlot"/>; reading, requeuing, and deferring a delivery reuse the same reserved slot
/// via <see cref="WriteReserved"/> and never change the counter.
/// </para>
/// <para>
/// <b>Channel.</b> The bounded channel backing this queue is written to exclusively through
/// <see cref="WriteReserved"/>, after a successful reservation. The invariant "channel item count is
/// less than or equal to <see cref="Occupancy"/>, which is less than or equal to <see cref="Capacity"/>"
/// means a correctly paired write can never fail — pairing every reservation with exactly one write is
/// the caller's responsibility.
/// </para>
/// <para>
/// <b>Latch.</b> <see cref="IsLatched"/> is set only when a full queue has no active consumer, or by an
/// explicit <see cref="TryLatch"/> call after a caller's failed wait-and-retry. It clears the instant a
/// release brings occupancy strictly below 50% of <see cref="Capacity"/>.
/// </para>
/// <para>
/// <b>Latch episodes.</b> The thread that wins the 0→1 latch transition — and whose immediate re-check
/// does not revert it — and the thread that wins the 1→0 transition are each notified exactly once,
/// through an optional <see cref="IInMemoryQueueLatchObserver"/> registered via
/// <see cref="SetLatchObserver"/>: <see cref="IInMemoryQueueLatchObserver.OnLatchSet"/> for the open,
/// <see cref="IInMemoryQueueLatchObserver.OnLatchCleared"/> for the matching close, both carrying the
/// same <see cref="InMemoryLatchEpisode"/> snapshot. Both notifications fire outside a short internal
/// lock taken only at the transition itself (the <see cref="TryReserve"/> fast path with free space is
/// untouched), and the close notification always fires after the occupancy counter change that
/// triggered it. A latch transition that reverts on its own immediate re-check (see <see cref="TryLatch"/>)
/// also closes any episode that happens to be open at that point — defends against the transient flag
/// itself briefly reading as set to an unrelated observer racing the same transition. Every open has
/// exactly one matching close, even under concurrent transitions: opening only happens while
/// <c>!open &amp;&amp; IsLatched</c>, and closing only while <c>open &amp;&amp; !IsLatched</c>, both
/// checked under the same lock.
/// </para>
/// <para>
/// <b>Active consumer.</b> A consumer counts as active from the moment its <see cref="ReadAllAsync"/>
/// enumerator starts running until it is cancelled, faults, or completes — a suspended handler between
/// deliveries still counts; a cancelled reader stops counting immediately, before its enumerator is
/// disposed.
/// </para>
/// <para>
/// <b>Close.</b> <see cref="Close"/> is a one-way transition: once <see cref="IsClosed"/> is set, no
/// waiter in <see cref="WaitToReserveAsync"/> is ever granted a slot again — every waiter queued at the
/// moment of the call, and every call that observes the flag afterwards, resolves with
/// <see cref="QueueWaitResult.Closed"/> instead. A reservation taken directly through
/// <see cref="TryReserve"/> is still honored even after closing (this queue does not gate admission
/// itself), but the write it produces is dropped on arrival: <see cref="WriteReserved"/> and both
/// <see cref="RequeueAtHead(InMemoryDelivery)"/> overloads check the flag after a successful write and,
/// when set, immediately drop that delivery — returning its buffer to the pool supplied to
/// <see cref="Close"/> and releasing its slot — rather than leaving it reachable in the channel. A drop
/// caused by such a late write that loses the race with a concurrent close AFTER the owning adapter has
/// already finished disposing is still counted toward <see cref="TakeDroppedAfterClose"/>, but is never
/// logged — only <c>Dispose</c> reports a single aggregated warning per queue, at the point it reads that
/// counter, and there is no later moment to log a subsequent drop against.
/// </para>
/// </remarks>
internal sealed class InMemoryQueue
{
    /// <summary>
    /// The largest timeout <see cref="WaitToReserveAsync"/> ever passes to the underlying timed wait.
    /// <see cref="Task.WaitAsync(TimeSpan, TimeProvider, CancellationToken)"/> throws
    /// <see cref="ArgumentOutOfRangeException"/> for a timeout at or above roughly 49.7 days
    /// (<see cref="uint.MaxValue"/> milliseconds); a caller-supplied timeout above this value is silently
    /// clamped down to it, before a waiter is even enqueued, rather than letting that exception surface
    /// after a waiter is already installed — which would otherwise strand it in the FIFO forever (a
    /// permanent queue-slot leak the first release to reach it would hand a slot to, without anyone ever
    /// observing the grant).
    /// </summary>
    internal static readonly TimeSpan MaxSupportedWaitTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly Channel<InMemoryDelivery> _channel;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _waitersLock = new();
    private readonly Lock _requeueLock = new();
    private readonly LinkedList<ReserveWaiter> _waiters = new();
    private int _occupancy;
    private int _latched;
    private int _activeConsumers;
    private int _waiterCount;
    private int _closed;
    private InMemoryBufferPool? _closedPool;
    private int _droppedAfterClose;
    private int _invariantViolationCount;
    private long _rejectedCopyCount;
    private readonly Lock _latchEpisodeLock = new();
    private bool _latchEpisodeOpen;
    private InMemoryLatchEpisode? _currentLatchEpisode;
    private IInMemoryQueueLatchObserver? _latchObserver;

    /// <param name="name">The queue's name. Must not be null or empty.</param>
    /// <param name="capacity">The queue's capacity. Must be greater than zero.</param>
    /// <param name="timeProvider">
    /// The time source used by <see cref="WaitToReserveAsync"/>. Defaults to <see cref="TimeProvider.System"/>.
    /// </param>
    internal InMemoryQueue(string name, int capacity, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        Name = name;
        Capacity = capacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _channel = Channel.CreateBounded<InMemoryDelivery>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Gets the queue's name.</summary>
    internal string Name { get; }

    /// <summary>Gets the queue's capacity — the upper bound for <see cref="Occupancy"/>.</summary>
    internal int Capacity { get; }

    /// <summary>Gets the number of slots currently occupied (reserved, in flight, or pending redelivery).</summary>
    internal int Occupancy => Volatile.Read(ref _occupancy);

    /// <summary>Gets whether the "full" latch is currently set.</summary>
    internal bool IsLatched => Volatile.Read(ref _latched) == 1;

    /// <summary>Gets the number of currently active consumers (non-cancelled <see cref="ReadAllAsync"/> readers).</summary>
    internal int ActiveConsumerCount => Volatile.Read(ref _activeConsumers);

    /// <summary>Gets whether at least one consumer is currently active.</summary>
    internal bool HasActiveConsumer => ActiveConsumerCount > 0;

    /// <summary>
    /// Gets whether this queue has been closed by <see cref="Close"/>. See this type's <c>Close</c>
    /// remarks for the full set of consequences.
    /// </summary>
    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Gets the number of times <see cref="DropLate"/> caught an invariant violation while dropping a
    /// late write instead of letting it propagate to the caller of <see cref="WriteReserved"/> or
    /// <see cref="RequeueAtHead(InMemoryDelivery)"/>. A test hook: always zero in the absence of a prior
    /// bug elsewhere that already broke this queue's "channel item count is less than or equal to
    /// <see cref="Occupancy"/>" invariant.
    /// </summary>
    internal int InvariantViolationCount => Volatile.Read(ref _invariantViolationCount);

    /// <summary>
    /// Gets the number of fan-out copies rejected against this queue because it was full or latched,
    /// recorded via <see cref="RecordRejectedCopy"/>. Read with <c>Interlocked.Read(ref long)</c> —
    /// this queue never resets it.
    /// </summary>
    internal long RejectedCopyCount => Interlocked.Read(ref _rejectedCopyCount);

    /// <summary>
    /// Records that one fan-out copy of a message was rejected against this queue because it was full or
    /// latched. Called by the send path's diagnostics after it observes such a rejection — this queue
    /// itself never calls it.
    /// </summary>
    internal void RecordRejectedCopy() => Interlocked.Increment(ref _rejectedCopyCount);

    /// <summary>
    /// Registers <paramref name="observer"/> to be notified of every latch episode this queue opens and
    /// closes from now on. "Last observer wins": a later call replaces any observer registered earlier,
    /// with no coordination between the two — the owning adapter is expected to call this once, at
    /// construction, for every queue it owns. <see langword="null"/> stops notifying entirely. Never
    /// throws.
    /// </summary>
    internal void SetLatchObserver(IInMemoryQueueLatchObserver? observer) => Volatile.Write(ref _latchObserver, observer);

    /// <summary>
    /// Gets whether at least one live (not yet granted, and not abandoned by a timeout or cancellation)
    /// waiter is currently queued for <see cref="WaitToReserveAsync"/>. A test hook: the fast path (space
    /// already available, or a slot already reserved) never creates a waiter, and a waiter stops
    /// counting the instant it is either granted a slot or abandons its wait — it does not need to still
    /// be physically dequeued for this to flip to <see langword="false"/>.
    /// </summary>
    internal bool HasPendingSpaceWaiter => Volatile.Read(ref _waiterCount) > 0;

    /// <summary>
    /// Gets the number of waiter entries physically linked in the FIFO, live or not. A test hook: an
    /// abandoned waiter unlinks itself immediately, so this never exceeds the live waiters by more than
    /// the ones abandoning at this very instant.
    /// </summary>
    internal int LinkedWaiterCount
    {
        get
        {
            lock (_waitersLock)
            {
                return _waiters.Count;
            }
        }
    }

    /// <summary>
    /// Attempts to reserve one slot without waiting. On <see cref="QueueReservationResult.Reserved"/>
    /// the caller MUST follow with exactly one <see cref="WriteReserved"/> (or release the slot with
    /// <see cref="ReleaseSlot"/> if it decides not to write after all).
    /// </summary>
    internal QueueReservationResult TryReserve()
    {
        while (true)
        {
            if (IsLatched)
            {
                return QueueReservationResult.Latched;
            }

            int occupancy = Volatile.Read(ref _occupancy);
            if (occupancy < Capacity)
            {
                if (Interlocked.CompareExchange(ref _occupancy, occupancy + 1, occupancy) == occupancy)
                {
                    return QueueReservationResult.Reserved;
                }

                continue;
            }

            if (HasActiveConsumer)
            {
                return QueueReservationResult.Full;
            }

            if (SetLatchAndRecheck())
            {
                return QueueReservationResult.Latched;
            }

            // the helper cleared the latch again immediately (a concurrent release already brought
            // occupancy below the release threshold): retry from the top, a slot may now be available.
        }
    }

    /// <summary>
    /// Writes <paramref name="delivery"/> to the bounded channel backing this queue. The caller MUST
    /// hold a reservation before calling this method — either a prior <see cref="TryReserve"/> (or
    /// <see cref="WaitToReserveAsync"/>) that returned <c>Reserved</c>, or a redelivery reusing the same
    /// already-reserved slot. Pairing reservations with writes is the caller's responsibility and is not
    /// enforced here (checking the channel's current item count would take a lock on the hot path).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The underlying channel itself is already at capacity — a write without a matching reservation, or
    /// more writes than reservations.
    /// </exception>
    internal void WriteReserved(InMemoryDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        if (!_channel.Writer.TryWrite(delivery))
        {
            throw new InvalidOperationException(
                $"Queue '{Name}' failed to write a reserved delivery: the channel is at capacity. " +
                "Every WriteReserved call must be paired with a prior reservation.");
        }

        if (Volatile.Read(ref _closed) != 0)
        {
            DropLate();
        }
    }

    /// <summary>
    /// Puts <paramref name="deliveries"/> back at the head of this queue, in the given order, ahead of
    /// every delivery that is in the channel when this call starts. Every delivery passed here MUST
    /// already hold a reserved slot (for example a delivery handed to a consumer and never settled), so
    /// <see cref="Occupancy"/> is not changed. Writing to the channel wakes any reader waiting for data.
    /// </summary>
    /// <remarks>
    /// Implemented by draining the channel, writing <paramref name="deliveries"/>, then writing the
    /// drained deliveries back in their original order. Requeues are serialized among themselves; the
    /// publish and read paths take no additional lock, so a delivery published concurrently while the
    /// channel is being drained may end up ahead of the requeued ones. This is an exceptional path; order
    /// under concurrency is not guaranteed.
    /// </remarks>
    /// <param name="deliveries">The deliveries to requeue, oldest first. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deliveries"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The channel cannot hold the requeued deliveries together with the drained ones — at least one of
    /// them was passed without a reservation. The drained deliveries are written back first, as far as
    /// the channel allows.
    /// </exception>
    internal void RequeueAtHead(IReadOnlyList<InMemoryDelivery> deliveries)
    {
        ArgumentNullException.ThrowIfNull(deliveries);

        if (deliveries.Count == 0)
        {
            return;
        }

        lock (_requeueLock)
        {
            ChannelReader<InMemoryDelivery> reader = _channel.Reader;
            ChannelWriter<InMemoryDelivery> writer = _channel.Writer;
            var drained = new List<InMemoryDelivery>(reader.Count);
            while (reader.TryRead(out InMemoryDelivery? queued))
            {
                drained.Add(queued);
            }

            if ((long)deliveries.Count + drained.Count > Capacity)
            {
                WriteBack(writer, drained, 0);
                throw RequeueOverflow();
            }

            for (int i = 0; i < deliveries.Count; i++)
            {
                if (!writer.TryWrite(deliveries[i]))
                {
                    WriteBack(writer, drained, 0);
                    throw RequeueOverflow();
                }
            }

            for (int i = 0; i < drained.Count; i++)
            {
                if (!writer.TryWrite(drained[i]))
                {
                    WriteBack(writer, drained, i + 1);
                    throw RequeueOverflow();
                }
            }
        }

        if (Volatile.Read(ref _closed) != 0)
        {
            DropLate();
        }
    }

    /// <summary>
    /// Puts <paramref name="delivery"/> back at the head of this queue, ahead of every delivery that is in
    /// the channel when this call starts. <paramref name="delivery"/> MUST already hold a reserved slot,
    /// so <see cref="Occupancy"/> is not changed. A single-delivery overload of
    /// <see cref="RequeueAtHead(IReadOnlyList{InMemoryDelivery})"/> for the common per-message requeue
    /// case (settlement's <c>Requeue</c> action), so that case does not need to allocate a one-element
    /// list just to call the list overload.
    /// </summary>
    /// <remarks>
    /// Same O(queue depth) cost and the same drain-under-lock-then-write-back implementation as the list
    /// overload — a bounded <see cref="System.Threading.Channels.Channel{T}"/> has no cheaper "prepend"
    /// operation. Bounded by <c>InMemoryTransportOptions.MaxRedeliveries</c> in practice, since every
    /// <c>Requeue</c> past that limit dead-letters instead of calling this method again.
    /// </remarks>
    /// <param name="delivery">The delivery to requeue. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="delivery"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The channel cannot hold <paramref name="delivery"/> together with the drained ones — it was passed
    /// without a matching reservation. The drained deliveries are written back first, as far as the
    /// channel allows.
    /// </exception>
    internal void RequeueAtHead(InMemoryDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        lock (_requeueLock)
        {
            ChannelReader<InMemoryDelivery> reader = _channel.Reader;
            ChannelWriter<InMemoryDelivery> writer = _channel.Writer;
            var drained = new List<InMemoryDelivery>(reader.Count);
            while (reader.TryRead(out InMemoryDelivery? queued))
            {
                drained.Add(queued);
            }

            if (1L + drained.Count > Capacity)
            {
                WriteBack(writer, drained, 0);
                throw RequeueOverflow();
            }

            if (!writer.TryWrite(delivery))
            {
                WriteBack(writer, drained, 0);
                throw RequeueOverflow();
            }

            for (int i = 0; i < drained.Count; i++)
            {
                if (!writer.TryWrite(drained[i]))
                {
                    WriteBack(writer, drained, i + 1);
                    throw RequeueOverflow();
                }
            }
        }

        if (Volatile.Read(ref _closed) != 0)
        {
            DropLate();
        }
    }

    /// <summary>
    /// Best-effort write-back of drained deliveries after a requeue found the channel over capacity, so
    /// deliveries holding reserved slots are not silently lost before the invariant violation is reported.
    /// </summary>
    private static void WriteBack(ChannelWriter<InMemoryDelivery> writer, List<InMemoryDelivery> drained, int start)
    {
        for (int i = start; i < drained.Count; i++)
        {
            if (!writer.TryWrite(drained[i]))
            {
                return;
            }
        }
    }

    private InvalidOperationException RequeueOverflow() =>
        new($"Queue '{Name}' cannot requeue deliveries at its head: the channel is at capacity. " +
            "Every requeued delivery must still hold its reserved slot.");

    /// <summary>
    /// Closes this queue: from this call on, no waiter in <see cref="WaitToReserveAsync"/> is ever
    /// granted a slot again. Every waiter currently queued is resolved with
    /// <see cref="QueueWaitResult.Closed"/> — without ever being handed a slot — and unlinked from the
    /// FIFO. Idempotent: a call that finds the queue already closed does nothing further (the pool and
    /// the waiter sweep already ran on the first, winning call).
    /// </summary>
    /// <param name="pool">
    /// The pool <see cref="DropRemaining"/> and <see cref="DropLate"/> return every buffer this queue
    /// drops after this call to. Stored before the closed flag is published, so any caller that observes
    /// the flag set is guaranteed to see this value.
    /// </param>
    internal void Close(InMemoryBufferPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);

        _closedPool = pool;
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        List<ReserveWaiter>? closed = null;
        lock (_waitersLock)
        {
            while (_waiters.First is { } node)
            {
                _waiters.RemoveFirst();
                ReserveWaiter candidate = node.Value;
                if (candidate.TryMarkClosed())
                {
                    Interlocked.Decrement(ref _waiterCount);
                    (closed ??= []).Add(candidate);
                }

                // Already granted or abandoned by a concurrent caller: that caller already adjusted
                // _waiterCount itself, so this sweep only needs to finish unlinking it from the FIFO.
            }
        }

        if (closed is not null)
        {
            // complete outside the waiters lock: continuations run asynchronously
            // (RunContinuationsAsynchronously), but there is no reason to hold the lock while scheduling.
            foreach (ReserveWaiter waiter in closed)
            {
                waiter.CompleteClosed();
            }
        }
    }

    /// <summary>
    /// Drains every delivery currently sitting in this queue's channel, returning each one's buffer to
    /// the pool supplied to <see cref="Close"/> before releasing its reserved slot, and adds the number
    /// dropped this way to the running total reported by <see cref="TakeDroppedAfterClose"/>. The channel
    /// itself is never completed — <see cref="WriteReserved"/> and <see cref="RequeueAtHead(InMemoryDelivery)"/>
    /// must keep accepting (and immediately dropping) a write that loses the race with a concurrent close
    /// rather than throwing. Safe to call repeatedly: once the channel is empty, further calls are no-ops
    /// that return zero.
    /// </summary>
    /// <returns>The number of deliveries dropped by this call.</returns>
    internal int DropRemaining()
    {
        // Guaranteed non-null: Close(pool) always stores the pool before publishing the closed flag, and
        // every caller reaching this method (Dispose, or WriteReserved/RequeueAtHead through DropLate)
        // only does so after observing that flag set.
        InMemoryBufferPool pool = _closedPool!;

        int dropped = 0;
        lock (_requeueLock)
        {
            ChannelReader<InMemoryDelivery> reader = _channel.Reader;
            while (reader.TryRead(out InMemoryDelivery? delivery))
            {
                pool.Return(delivery.Buffer);
                ReleaseSlot();
                dropped++;
            }
        }

        if (dropped > 0)
        {
            Interlocked.Add(ref _droppedAfterClose, dropped);
        }

        return dropped;
    }

    /// <summary>
    /// Atomically reads and resets the running total of deliveries dropped by <see cref="DropRemaining"/>
    /// (directly, or through <see cref="DropLate"/>) since the last call. <c>Dispose</c> calls this once
    /// per queue, at the very end of its own shutdown sequence, to report a single aggregated warning.
    /// </summary>
    /// <returns>The number of deliveries dropped since the previous call.</returns>
    internal int TakeDroppedAfterClose() => Interlocked.Exchange(ref _droppedAfterClose, 0);

    /// <summary>
    /// Drops every delivery currently in the channel after a write lost the race with a concurrent
    /// <see cref="Close"/> — called by <see cref="WriteReserved"/> and both <see cref="RequeueAtHead(InMemoryDelivery)"/>
    /// overloads once they observe the closed flag set right after a successful write. Calls only
    /// <see cref="DropRemaining"/>: no callback, no logging, no metric — those belong to <c>Dispose</c>,
    /// which reads the aggregated count later through <see cref="TakeDroppedAfterClose"/>. Never lets an
    /// exception reach its caller: a write that already completed successfully must return normally, or
    /// the caller's own error-handling path could return the same buffer to the pool a second time.
    /// <see cref="MethodImplOptions.NoInlining"/> keeps this rare, defensive path out of the hot,
    /// per-write inline body.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DropLate()
    {
        try
        {
            DropRemaining();
        }
        catch (InvalidOperationException)
        {
            // This can only happen after a prior bug elsewhere already broke the "channel item count is
            // less than or equal to Occupancy" invariant (for example ReleaseSlot finding no occupied
            // slot to release). Counted for diagnosis, never logged or exposed as a metric — this method
            // must stay free of any dependency a rare defensive catch could turn into a second failure.
            Interlocked.Increment(ref _invariantViolationCount);
        }
    }

    /// <summary>
    /// Releases one occupied slot after a delivery has been settled (acknowledged, rejected, or
    /// otherwise abandoned). When a live waiter is queued in <see cref="WaitToReserveAsync"/>, the freed
    /// slot is handed DIRECTLY to the oldest one instead of being released back to the pool — occupancy
    /// stays unchanged (ownership merely transfers) and no other waiter is woken. Only when no live
    /// waiter is queued does this fall back to the compare-and-swap decrement, so a concurrent reservation
    /// can never observe the counter at a transient negative value.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call cleared the latch (occupancy dropped strictly below 50% of
    /// <see cref="Capacity"/>); <see langword="false"/> otherwise (including every hand-off to a waiter,
    /// since occupancy never changes on that path).
    /// </returns>
    /// <exception cref="InvalidOperationException">The queue has no occupied slot to release.</exception>
    internal bool ReleaseSlot()
    {
        ReserveWaiter? granted = TryGrantToWaiter();
        if (granted is not null)
        {
            // complete the waiter's task outside the waiters lock: continuations run asynchronously
            // (RunContinuationsAsynchronously), but there is no reason to hold the lock while scheduling.
            granted.Grant();
            return false;
        }

        int occupancyAfter;
        while (true)
        {
            int occupancyBefore = Volatile.Read(ref _occupancy);
            if (occupancyBefore == 0)
            {
                throw new InvalidOperationException($"Queue '{Name}' has no occupied slot to release.");
            }

            occupancyAfter = occupancyBefore - 1;
            if (Interlocked.CompareExchange(ref _occupancy, occupancyAfter, occupancyBefore) == occupancyBefore)
            {
                break;
            }
        }

        // A waiter may have been installed after the waiter-count check above yet re-checked occupancy
        // before the decrement landed: it saw a full queue and is now waiting for the slot just freed.
        // Both sides publish their counter with a full fence before reading the other one, so at least
        // one of them observes the other — re-offer the freed slot to such a late waiter. A hand-off
        // restores the occupancy this release started from, exactly like the direct hand-off above, so
        // the latch is only considered once no waiter took the slot.
        if (HandOffToLateWaiter())
        {
            return false;
        }

        // re-read rather than reuse occupancyAfter: a hand-off attempt may have run in between
        return TryClearLatch(Volatile.Read(ref _occupancy));
    }

    /// <summary>
    /// Re-offers a slot freed by <see cref="ReleaseSlot"/> to a waiter that was installed concurrently
    /// with the release and would otherwise sleep until its timeout despite free space. Takes the slot
    /// back on the waiter's behalf and grants it; when no live waiter is found after all (every counted
    /// waiter was abandoning at the same instant), gives the slot back and re-checks.
    /// </summary>
    /// <returns><see langword="true"/> when the freed slot was handed to a waiter.</returns>
    private bool HandOffToLateWaiter()
    {
        SpinWait spinner = default;
        while (Volatile.Read(ref _waiterCount) > 0)
        {
            int occupancy = Volatile.Read(ref _occupancy);
            if (occupancy >= Capacity)
            {
                // a concurrent reservation already consumed the freed slot; a later release serves the waiter
                return false;
            }

            if (Interlocked.CompareExchange(ref _occupancy, occupancy + 1, occupancy) != occupancy)
            {
                continue;
            }

            ReserveWaiter? granted = TryGrantToWaiter();
            if (granted is not null)
            {
                granted.Grant();
                return true;
            }

            // give the slot back; ReleaseSlot considers the latch once this returns
            Interlocked.Decrement(ref _occupancy);
            spinner.SpinOnce();
        }

        return false;
    }

    /// <summary>
    /// Clears the latch when <paramref name="occupancy"/> is strictly below 50% of
    /// <see cref="Capacity"/>. Returns <see langword="true"/> when this call cleared it.
    /// </summary>
    private bool TryClearLatch(int occupancy)
    {
        bool cleared =
            Volatile.Read(ref _latched) == 1
            && 2L * occupancy < Capacity
            && Interlocked.CompareExchange(ref _latched, 0, 1) == 1;

        if (cleared)
        {
            TryCloseLatchEpisode();
        }

        return cleared;
    }

    /// <summary>
    /// Dequeues waiters from the FIFO, in order, until one is successfully transitioned from pending to
    /// granted (skipping any already abandoned by a timeout or cancellation), and returns it — or
    /// <see langword="null"/> when no live waiter is queued. The fast path (no waiters at all) is a
    /// single volatile read with no lock and no allocation.
    /// </summary>
    private ReserveWaiter? TryGrantToWaiter()
    {
        if (Volatile.Read(ref _waiterCount) == 0)
        {
            return null;
        }

        lock (_waitersLock)
        {
            while (_waiters.First is { } node)
            {
                _waiters.RemoveFirst();
                ReserveWaiter candidate = node.Value;
                if (candidate.TryMarkGranted())
                {
                    Interlocked.Decrement(ref _waiterCount);
                    return candidate;
                }

                // abandoned by a concurrent timeout/cancellation that has not unlinked it yet — its count
                // was decremented when it was abandoned; drop it from the FIFO and keep looking.
            }
        }

        return null;
    }

    /// <summary>
    /// Sets the latch after the caller's single wait-and-retry attempt failed. Idempotent and
    /// race-safe: immediately re-observes the release threshold after setting the flag so the latch is
    /// never left set on a queue that has already drained below 50% capacity in the interim.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the latch is set after this call (whether it was set by this call or
    /// a concurrent one); <see langword="false"/> when this call set it and then, on immediate re-check,
    /// found occupancy already below the release threshold and cleared it again.
    /// </returns>
    internal bool TryLatch() => SetLatchAndRecheck();

    /// <summary>
    /// Waits, at most until <paramref name="timeout"/> elapses, for a slot to become reservable, and
    /// reserves it as part of the same wait. Implemented as FIFO per-waiter direct slot hand-off (see
    /// <see cref="ReleaseSlot"/>): at most one waiter is ever woken per released slot, and a wakeup never
    /// resolves without also holding the reservation — there is no thundering-herd wakeup to retry
    /// against, so at most one wait against the FIFO is ever needed per call.
    /// </summary>
    /// <param name="timeout">The maximum time to wait. Must not be negative; zero polls once.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>
    /// <see cref="QueueWaitResult.Reserved"/> when a slot was reserved before the deadline — the caller
    /// MUST follow with exactly one <see cref="WriteReserved"/>; <see cref="QueueWaitResult.TimedOut"/>
    /// when no slot became available in time; <see cref="QueueWaitResult.Cancelled"/> when
    /// <paramref name="cancellationToken"/> was cancelled; <see cref="QueueWaitResult.Latched"/> when the
    /// latch was observed set; <see cref="QueueWaitResult.Closed"/> when this queue was, or became while
    /// waiting, closed by <see cref="Close"/> — no slot is ever held in that case.
    /// </returns>
    internal async ValueTask<QueueWaitResult> WaitToReserveAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        // Clamp before anything below enqueues a waiter — see MaxSupportedWaitTimeout.
        if (timeout > MaxSupportedWaitTimeout)
        {
            timeout = MaxSupportedWaitTimeout;
        }

        // (a) Checked before anything else creates a waiter: a queue closed before this call started
        // must always reject, regardless of whether TryReserve below would otherwise have succeeded.
        if (IsClosed)
        {
            return QueueWaitResult.Closed;
        }

        QueueReservationResult fast = TryReserve();
        if (fast == QueueReservationResult.Reserved)
        {
            return QueueWaitResult.Reserved;
        }

        if (fast == QueueReservationResult.Latched)
        {
            return QueueWaitResult.Latched;
        }

        if (timeout == TimeSpan.Zero)
        {
            return QueueWaitResult.TimedOut;
        }

        ReserveWaiter waiter = EnqueueWaiter();

        // (b) Checked immediately after enqueuing, before the re-check TryReserve below: Close publishes
        // its flag before sweeping the FIFO under the same lock EnqueueWaiter uses to add to it, and reads
        // the flag only after releasing that lock — so either this call sees the flag here, or Close's own
        // sweep sees the waiter just linked. Either way, neither side loses the race and strands a waiter.
        if (IsClosed)
        {
            if (TryAbandon(waiter))
            {
                return QueueWaitResult.Closed;
            }

            // the waiter was already granted a slot, or already marked closed, by a concurrent caller.
            return waiter.WasClosed ? QueueWaitResult.Closed : QueueWaitResult.Reserved;
        }

        // re-check after enqueuing: protects against a release signalled between the failed TryReserve
        // above and the waiter's installation (a lost-wakeup guard).
        QueueReservationResult afterEnqueue = TryReserve();
        if (afterEnqueue == QueueReservationResult.Reserved)
        {
            if (TryAbandon(waiter))
            {
                return QueueWaitResult.Reserved;
            }

            // the waiter was already granted a slot, or closed, by a concurrent caller: the caller of this
            // method would now hold two reservations (or a stale one) for one logical request. Release the
            // one just reserved above (it may be handed straight to the next waiter) and report the
            // waiter's own outcome instead.
            ReleaseSlot();
            return waiter.WasClosed ? QueueWaitResult.Closed : QueueWaitResult.Reserved;
        }

        if (afterEnqueue == QueueReservationResult.Latched)
        {
            if (TryAbandon(waiter))
            {
                return QueueWaitResult.Latched;
            }

            // the waiter was granted a slot, or closed, concurrently, an instant before the latch was
            // observed set: the caller owns that outcome and must not leak it.
            return waiter.WasClosed ? QueueWaitResult.Closed : QueueWaitResult.Reserved;
        }

        // (c) waiter.Task is Task<bool> (true = granted, false = closed), but the timed wait below is
        // awaited on the non-generic Task base with ConfigureAwaitOptions.SuppressThrowing — awaiting a
        // Task<TResult> with that option throws ArgumentOutOfRangeException at run time. waitTask MUST
        // stay typed as the base Task; once it completes successfully, the actual outcome is read back
        // from waiter.Task.Result instead.
        Task waitTask;
        try
        {
            waitTask = waiter.Task.WaitAsync(timeout, _timeProvider, cancellationToken);
            await waitTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (Exception)
        {
            // Defensive: timeout is already clamped to what the timer supports, so this is not expected
            // to throw in practice. If it ever does, the waiter must not be left installed — a stranded
            // live waiter would silently absorb the next released slot forever.
            TryAbandon(waiter);
            throw;
        }

        if (waitTask.IsCompletedSuccessfully)
        {
            return waiter.Task.Result ? QueueWaitResult.Reserved : QueueWaitResult.Closed;
        }

        if (TryAbandon(waiter))
        {
            return waitTask.IsCanceled ? QueueWaitResult.Cancelled : QueueWaitResult.TimedOut;
        }

        // the slot was granted, or the queue was closed, concurrently with the timeout/cancellation: the
        // caller owns that outcome now and must not leak it.
        return waiter.WasClosed ? QueueWaitResult.Closed : QueueWaitResult.Reserved;
    }

    /// <summary>
    /// Reads deliveries from this queue's bounded channel, registering the calling reader as an active
    /// consumer for as long as the returned enumerator is being iterated. Cancelling
    /// <paramref name="cancellationToken"/> deactivates the registration immediately (even before the
    /// enumerator is disposed) and then propagates <see cref="OperationCanceledException"/> out of the
    /// next <c>MoveNextAsync</c> — any delivery still pending in the channel at that point is left
    /// untouched.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the wait for the next delivery and deactivates this reader's active-consumer registration.
    /// Typically a token linking the caller's own token with a transport-wide shutdown token.
    /// </param>
    /// <param name="stopToken">
    /// An additional guard checked, via <see cref="CancellationToken.ThrowIfCancellationRequested"/>,
    /// immediately before every <c>TryRead</c> attempt (including the one right after
    /// <c>WaitToReadAsync</c> returns) — alongside, not instead of, <paramref name="cancellationToken"/>.
    /// It exists to close a narrow race: <see cref="CancellationTokenSource.CancelAsync"/> marks its token
    /// cancelled and only then runs its registered callbacks, in LIFO order. When a consumer registered on
    /// the SAME original token this reader's <paramref name="cancellationToken"/> is linked from reacts to
    /// that cancellation inline — for example by requeuing its own in-flight delivery — and thereby wakes
    /// this reader, the linked token's own callback (which would propagate the cancellation to
    /// <paramref name="cancellationToken"/>) may not have run yet, even though the original token already
    /// reports <see cref="CancellationToken.IsCancellationRequested"/> as <see langword="true"/>. Passing
    /// that original token here lets this reader observe the cancellation immediately — a plain flag read,
    /// independent of callback ordering — and refuse to read the just-requeued delivery back out, instead
    /// of taking it and only bumping its redelivery count for no reason. Defaults to
    /// <see langword="default"/> (never cancelled) for callers with no such original token to guard
    /// against.
    /// </param>
    internal async IAsyncEnumerable<InMemoryDelivery> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        CancellationToken stopToken = default)
    {
        Interlocked.Increment(ref _activeConsumers);
        var registration = new ConsumerRegistration(this);
        CancellationTokenRegistration ctr = cancellationToken.Register(
            static state => ((ConsumerRegistration)state!).Deactivate(), registration);
        try
        {
            ChannelReader<InMemoryDelivery> reader = _channel.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stopToken.ThrowIfCancellationRequested();
                    if (!reader.TryRead(out InMemoryDelivery? delivery))
                    {
                        break;
                    }

                    yield return delivery;
                }
            }
        }
        finally
        {
            ctr.Dispose();
            registration.Deactivate();
        }
    }

    /// <summary>
    /// Sets the latch (or observes it already set) and immediately re-reads the occupancy counter,
    /// closing the race between setting the latch and a concurrent <see cref="ReleaseSlot"/> draining
    /// the queue below the release threshold at the same instant.
    /// </summary>
    private bool SetLatchAndRecheck()
    {
        if (Interlocked.CompareExchange(ref _latched, 1, 0) == 1)
        {
            // already latched by a concurrent caller
            return true;
        }

        if (2L * Volatile.Read(ref _occupancy) < Capacity)
        {
            Interlocked.CompareExchange(ref _latched, 0, 1);

            // ABA guard: this call's own set-then-immediately-revert may have raced a concurrent
            // ReleaseSlot's own close attempt for a DIFFERENT (already-open) episode in a way that left
            // it unable to close (see this type's remarks on latch episodes) — closing here as well is a
            // no-op whenever there is nothing open, and closes it correctly whenever there is.
            TryCloseLatchEpisode();
            return false;
        }

        // Won the 0→1 transition and the immediate re-check did not revert it: this call — and only this
        // call — opens the episode.
        TryOpenLatchEpisode();
        return true;
    }

    /// <summary>
    /// Opens a new latch episode and notifies the registered <see cref="IInMemoryQueueLatchObserver"/>,
    /// but only when none is already open for a currently-set latch — see this type's remarks on latch
    /// episodes for the exact race-safety rule. A no-op when no observer is registered.
    /// </summary>
    private void TryOpenLatchEpisode()
    {
        IInMemoryQueueLatchObserver? observer;
        InMemoryLatchEpisode episode;
        lock (_latchEpisodeLock)
        {
            if (_latchEpisodeOpen || !IsLatched)
            {
                return;
            }

            observer = Volatile.Read(ref _latchObserver);
            episode = new InMemoryLatchEpisode
            {
                StartTimestamp = SafeGetTimestamp(observer),
                RejectedCopiesAtStart = RejectedCopyCount,
            };

            _currentLatchEpisode = episode;
            _latchEpisodeOpen = true;
        }

        observer?.OnLatchSet(this, episode);
    }

    /// <summary>
    /// Closes the currently open latch episode and notifies the registered
    /// <see cref="IInMemoryQueueLatchObserver"/>, but only when one is actually open for a currently-clear
    /// latch — see this type's remarks on latch episodes for the exact race-safety rule. A no-op when no
    /// episode is open, or when no observer is registered.
    /// </summary>
    private void TryCloseLatchEpisode()
    {
        IInMemoryQueueLatchObserver? observer;
        InMemoryLatchEpisode? episode;
        lock (_latchEpisodeLock)
        {
            if (!_latchEpisodeOpen || IsLatched)
            {
                return;
            }

            observer = Volatile.Read(ref _latchObserver);
            episode = _currentLatchEpisode;
            _currentLatchEpisode = null;
            _latchEpisodeOpen = false;
        }

        if (episode is not null)
        {
            observer?.OnLatchCleared(this, episode);
        }
    }

    /// <summary>
    /// Calls <paramref name="observer"/>'s <see cref="IInMemoryQueueLatchObserver.GetTimestamp"/>,
    /// defensively: that member is documented to never throw, but a misbehaving implementation must never
    /// be able to corrupt this queue's own latch-episode bookkeeping. Returns zero for a
    /// <see langword="null"/> observer or a throwing call.
    /// </summary>
    private static long SafeGetTimestamp(IInMemoryQueueLatchObserver? observer)
    {
        if (observer is null)
        {
            return 0;
        }

        try
        {
            return observer.GetTimestamp();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Creates a new waiter, enqueues it at the tail of the FIFO, and tracks it as live via
    /// <see cref="_waiterCount"/>. An abandoned waiter unlinks itself immediately (see
    /// <see cref="TryAbandon"/>), so the FIFO never accumulates dead waiters behind a live head.
    /// </summary>
    private ReserveWaiter EnqueueWaiter()
    {
        var waiter = new ReserveWaiter();
        lock (_waitersLock)
        {
            _waiters.AddLast(waiter.Node);
        }

        Interlocked.Increment(ref _waiterCount);
        return waiter;
    }

    /// <summary>
    /// Transitions <paramref name="waiter"/> from pending to abandoned and unlinks it from the FIFO at
    /// once. Returns <see langword="false"/> when a concurrent <see cref="ReleaseSlot"/> already granted
    /// it a slot — the caller then owns that reservation and must not leak it.
    /// </summary>
    private bool TryAbandon(ReserveWaiter waiter)
    {
        if (!waiter.TryMarkAbandoned())
        {
            return false;
        }

        Interlocked.Decrement(ref _waiterCount);
        lock (_waitersLock)
        {
            if (waiter.Node.List is not null)
            {
                _waiters.Remove(waiter.Node);
            }
        }

        return true;
    }

    /// <summary>
    /// A single caller's slot in <see cref="WaitToReserveAsync"/>'s FIFO. Its state transitions exactly
    /// once, from pending to either granted (a released slot was handed directly to it) or abandoned (its
    /// caller's timeout or cancellation lost the race), and both transitions are mutually exclusive
    /// compare-and-swap operations so a concurrent <see cref="ReleaseSlot"/> and a concurrent
    /// timeout/cancellation can never both believe they own the outcome.
    /// </summary>
    private sealed class ReserveWaiter
    {
        private const int Pending = 0;
        private const int Granted = 1;
        private const int Abandoned = 2;
        private const int Closed = 3;

        private readonly TaskCompletionSource<bool> _source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _state;

        internal ReserveWaiter() => Node = new LinkedListNode<ReserveWaiter>(this);

        /// <summary>Gets the FIFO node carrying this waiter, so an abandoned waiter can unlink itself in O(1).</summary>
        internal LinkedListNode<ReserveWaiter> Node { get; }

        /// <summary>
        /// Gets the underlying task. Its result distinguishes a grant (<see langword="true"/>, set by
        /// <see cref="Grant"/>) from a close (<see langword="false"/>, set by <see cref="CompleteClosed"/>)
        /// — a task that completes successfully at all means a slot was reserved for a grant, but never
        /// for a close.
        /// </summary>
        internal Task<bool> Task => _source.Task;

        /// <summary>
        /// Attempts to transition this waiter from pending to granted. Does not complete the underlying
        /// task — call <see cref="Grant"/> for that, outside of any lock.
        /// </summary>
        internal bool TryMarkGranted() => Interlocked.CompareExchange(ref _state, Granted, Pending) == Pending;

        /// <summary>
        /// Completes the underlying task with <see langword="true"/> after a successful
        /// <see cref="TryMarkGranted"/>. Kept separate so <see cref="ReleaseSlot"/> can complete it after
        /// releasing the waiters lock.
        /// </summary>
        internal void Grant() => _source.TrySetResult(true);

        /// <summary>
        /// Attempts to transition this waiter from pending to abandoned. Returns <see langword="false"/>
        /// when the waiter was already granted a slot by a concurrent <see cref="ReleaseSlot"/>, or
        /// already closed by a concurrent <see cref="Close"/> — the caller must then inspect
        /// <see cref="WasClosed"/> to tell which, since either way it must not leak a reservation it does
        /// not actually hold.
        /// </summary>
        internal bool TryMarkAbandoned() => Interlocked.CompareExchange(ref _state, Abandoned, Pending) == Pending;

        /// <summary>
        /// Attempts to transition this waiter from pending to closed. Returns <see langword="false"/>
        /// when the waiter was already granted a slot, or already abandoned, by a concurrent caller.
        /// </summary>
        internal bool TryMarkClosed() => Interlocked.CompareExchange(ref _state, Closed, Pending) == Pending;

        /// <summary>Gets whether this waiter's state is <see cref="Closed"/>.</summary>
        internal bool WasClosed => Volatile.Read(ref _state) == Closed;

        /// <summary>
        /// Completes the underlying task with <see langword="false"/> after a successful
        /// <see cref="TryMarkClosed"/>, so <see cref="WaitToReserveAsync"/> reports
        /// <see cref="QueueWaitResult.Closed"/> without this waiter ever having reserved a slot.
        /// </summary>
        internal void CompleteClosed() => _source.TrySetResult(false);
    }

    private sealed class ConsumerRegistration(InMemoryQueue queue)
    {
        private int _active = 1;

        internal void Deactivate()
        {
            if (Interlocked.Exchange(ref _active, 0) == 1)
            {
                Interlocked.Decrement(ref queue._activeConsumers);
            }
        }
    }
}
