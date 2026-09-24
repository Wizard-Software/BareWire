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
/// <b>Active consumer.</b> A consumer counts as active from the moment its <see cref="ReadAllAsync"/>
/// enumerator starts running until it is cancelled, faults, or completes — a suspended handler between
/// deliveries still counts; a cancelled reader stops counting immediately, before its enumerator is
/// disposed.
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
    private bool TryClearLatch(int occupancy) =>
        Volatile.Read(ref _latched) == 1
        && 2L * occupancy < Capacity
        && Interlocked.CompareExchange(ref _latched, 0, 1) == 1;

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
    /// latch was observed set.
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

        // re-check after enqueuing: protects against a release signalled between the failed TryReserve
        // above and the waiter's installation (a lost-wakeup guard).
        QueueReservationResult afterEnqueue = TryReserve();
        if (afterEnqueue == QueueReservationResult.Reserved)
        {
            if (TryAbandon(waiter))
            {
                return QueueWaitResult.Reserved;
            }

            // the waiter was already granted a slot by a concurrent ReleaseSlot: the caller would now
            // hold two reservations for one logical request. Release the one just reserved above (it may
            // be handed straight to the next waiter) and return Reserved for the one the waiter owns.
            ReleaseSlot();
            return QueueWaitResult.Reserved;
        }

        if (afterEnqueue == QueueReservationResult.Latched)
        {
            if (TryAbandon(waiter))
            {
                return QueueWaitResult.Latched;
            }

            // the waiter was granted a slot concurrently, an instant before the latch was observed set:
            // the caller owns that reservation and must not leak it.
            return QueueWaitResult.Reserved;
        }

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
            return QueueWaitResult.Reserved;
        }

        if (TryAbandon(waiter))
        {
            return waitTask.IsCanceled ? QueueWaitResult.Cancelled : QueueWaitResult.TimedOut;
        }

        // the slot was granted concurrently with the timeout/cancellation: the caller owns it now and
        // must not leak it.
        return QueueWaitResult.Reserved;
    }

    /// <summary>
    /// Reads deliveries from this queue's bounded channel, registering the calling reader as an active
    /// consumer for as long as the returned enumerator is being iterated. Cancelling
    /// <paramref name="cancellationToken"/> deactivates the registration immediately (even before the
    /// enumerator is disposed) and then propagates <see cref="OperationCanceledException"/> out of the
    /// next <c>MoveNextAsync</c> — any delivery still pending in the channel at that point is left
    /// untouched.
    /// </summary>
    internal async IAsyncEnumerable<InMemoryDelivery> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
            return false;
        }

        return true;
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

        private readonly TaskCompletionSource _source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _state;

        internal ReserveWaiter() => Node = new LinkedListNode<ReserveWaiter>(this);

        /// <summary>Gets the FIFO node carrying this waiter, so an abandoned waiter can unlink itself in O(1).</summary>
        internal LinkedListNode<ReserveWaiter> Node { get; }

        internal Task Task => _source.Task;

        /// <summary>
        /// Attempts to transition this waiter from pending to granted. Does not complete the underlying
        /// task — call <see cref="Grant"/> for that, outside of any lock.
        /// </summary>
        internal bool TryMarkGranted() => Interlocked.CompareExchange(ref _state, Granted, Pending) == Pending;

        /// <summary>
        /// Completes the underlying task after a successful <see cref="TryMarkGranted"/>. Kept separate
        /// so <see cref="ReleaseSlot"/> can complete it after releasing the waiters lock.
        /// </summary>
        internal void Grant() => _source.TrySetResult();

        /// <summary>
        /// Attempts to transition this waiter from pending to abandoned. Returns <see langword="false"/>
        /// when the waiter was already granted a slot by a concurrent <see cref="ReleaseSlot"/> — the
        /// caller then owns that reservation and must not leak it.
        /// </summary>
        internal bool TryMarkAbandoned() => Interlocked.CompareExchange(ref _state, Abandoned, Pending) == Pending;
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
