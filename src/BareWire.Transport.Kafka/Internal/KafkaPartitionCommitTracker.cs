using System.Collections.Concurrent;
using Confluent.Kafka;

namespace BareWire.Transport.Kafka.Internal;

/// <summary>
/// Tracks delivered-but-unsettled offsets per partition so that the stored commit position only
/// advances over a contiguous prefix of forward-settled messages (B17).
/// </summary>
/// <remarks>
/// A Kafka stored offset is a partition position ("everything below is processed"), not an
/// acknowledgement of a single message. Storing <c>offset + 1</c> for an acked message while a lower
/// offset was requeued would silently lose the requeued message. This tracker therefore stores a
/// position only when the contiguous prefix of forward-settled offsets advances, and pins the
/// position at the lowest held offset after a backward settlement. Safe for concurrent use: the poll
/// thread calls <see cref="Track"/>, settlers call <see cref="Complete"/>/<see cref="Hold"/>, and the
/// rebalance handler calls <see cref="Revoke"/>.
/// </remarks>
internal sealed class KafkaPartitionCommitTracker
{
    /// <summary>Upper bound accepted for the per-partition tracking limit (SEC-9).</summary>
    internal const int MaxTrackedPerPartitionCeiling = 1_048_576;

    private readonly string _topic;
    private readonly int _maxTracked;
    private readonly long _hardMaxTracked;
    private readonly Action<TopicPartitionOffset> _storeOffset;
    private readonly ConcurrentDictionary<int, PartitionState> _partitions = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaPartitionCommitTracker"/> class.
    /// </summary>
    /// <param name="topic">The topic whose partitions are tracked (one topic per consumer).</param>
    /// <param name="maxTrackedPerPartition">
    /// The back-pressure limit: the number of tracked offsets per partition at which the caller must
    /// pause the partition. The hard ceiling (2x this value) bounds memory.
    /// </param>
    /// <param name="storeOffset">
    /// Invoked under the partition lock to store a new commit position (e.g. <c>IConsumer.StoreOffset</c>),
    /// so a stored position never moves backwards.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxTrackedPerPartition"/> is below 1 or above <see cref="MaxTrackedPerPartitionCeiling"/>.
    /// </exception>
    internal KafkaPartitionCommitTracker(
        string topic, int maxTrackedPerPartition, Action<TopicPartitionOffset> storeOffset)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(storeOffset);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedPerPartition, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxTrackedPerPartition, MaxTrackedPerPartitionCeiling);

        _topic = topic;
        _maxTracked = maxTrackedPerPartition;
        _hardMaxTracked = 2L * maxTrackedPerPartition;
        _storeOffset = storeOffset;
    }

    /// <summary>
    /// Poll thread: registers a delivered offset. Returns <see langword="true"/> when the partition
    /// reached the back-pressure limit and the caller must pause it.
    /// </summary>
    /// <param name="partition">The partition number.</param>
    /// <param name="offset">The delivered offset.</param>
    /// <returns><see langword="true"/> when the caller must pause the partition.</returns>
    internal bool Track(int partition, long offset)
    {
        PartitionState state = _partitions.GetOrAdd(partition, static _ => new PartitionState());

        lock (state)
        {
            if (state.Revoked)
            {
                return false;
            }

            if (state.HeldAt is { } heldAt && offset >= heldAt)
            {
                // Above the pinned position: it will be redelivered after a restart anyway.
                return false;
            }

            if (offset <= state.HighestTracked)
            {
                // Non-monotonic offset within the assignment (e.g. an OFFSET_OUT_OF_RANGE reset):
                // the safe direction is to pin the commit position at this offset.
                HoldLocked(state, offset);
                return false;
            }

            if (state.Pending.Count >= _hardMaxTracked)
            {
                // Messages delivered despite the pause (librdkafka local fetch queue) exceeded the
                // hard ceiling: stop tracking and pin the commit at the first untracked offset.
                HoldLocked(state, offset);
                return true;
            }

            state.Pending.Enqueue(offset);
            state.HighestTracked = offset;
            return state.Pending.Count >= _maxTracked;
        }
    }

    /// <summary>
    /// Forward settlement (Ack, republish-then-store, Reject without DLQ). Stores and returns the new
    /// commit position when the contiguous prefix advanced; otherwise <see langword="null"/>
    /// (out-of-order completion, held partition, unknown/revoked partition, untracked offset).
    /// </summary>
    /// <param name="partition">The partition number.</param>
    /// <param name="offset">The settled offset.</param>
    /// <returns>The newly stored commit position, or <see langword="null"/> when nothing was stored.</returns>
    internal long? Complete(int partition, long offset)
    {
        if (!_partitions.TryGetValue(partition, out PartitionState? state))
        {
            return null;
        }

        lock (state)
        {
            if (state.Revoked
                || (state.HeldAt is { } heldAt && offset >= heldAt)
                || state.Pending.Count == 0
                || offset < state.Pending.Peek()
                || offset > state.HighestTracked)
            {
                return null;
            }

            if (offset != state.Pending.Peek())
            {
                state.CompletedOutOfOrder.Add(offset);
                return null;
            }

            state.Pending.Dequeue();
            while (state.Pending.Count > 0 && state.CompletedOutOfOrder.Remove(state.Pending.Peek()))
            {
                state.Pending.Dequeue();
            }

            long position = state.Pending.Count > 0
                ? state.Pending.Peek()
                : state.HeldAt ?? state.HighestTracked + 1;

            if (position <= state.LastStored)
            {
                return null;
            }

            _storeOffset(new TopicPartitionOffset(_topic, new Partition(partition), new Offset(position)));
            state.LastStored = position;
            return position;
        }
    }

    /// <summary>
    /// Backward settlement (Requeue, Nack without retry/DLQ, dropped/abandoned delivery): pins the
    /// commit position of the partition at <paramref name="offset"/> for the rest of the assignment
    /// and stops tracking offsets above it. Returns <see langword="true"/> on the first hold of the
    /// partition in this assignment (the caller logs a Warning once per partition).
    /// </summary>
    /// <param name="partition">The partition number.</param>
    /// <param name="offset">The offset that must not be committed past.</param>
    /// <returns><see langword="true"/> on the first hold of the partition in this assignment.</returns>
    internal bool Hold(int partition, long offset)
    {
        if (!_partitions.TryGetValue(partition, out PartitionState? state))
        {
            return false;
        }

        lock (state)
        {
            return !state.Revoked && HoldLocked(state, offset);
        }
    }

    /// <summary>Poll thread: <see langword="true"/> when a paused partition dropped below the limit.</summary>
    /// <param name="partition">The partition number.</param>
    /// <returns><see langword="true"/> when the partition may be resumed.</returns>
    internal bool HasCapacity(int partition)
    {
        if (!_partitions.TryGetValue(partition, out PartitionState? state))
        {
            return true;
        }

        lock (state)
        {
            return state.Revoked || state.Pending.Count < _maxTracked;
        }
    }

    /// <summary>Rebalance revoke (also invoked for lost partitions): marks and forgets the partitions.</summary>
    /// <param name="partitions">The revoked partition numbers.</param>
    internal void Revoke(IEnumerable<int> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);

        foreach (int partition in partitions)
        {
            if (_partitions.TryRemove(partition, out PartitionState? state))
            {
                // A late Complete/Hold that already fetched this state must not store or hold anything.
                lock (state)
                {
                    state.Revoked = true;
                }
            }
        }
    }

    /// <summary>Test/diagnostic probe: number of delivered-but-not-yet-committable offsets.</summary>
    /// <param name="partition">The partition number.</param>
    /// <returns>The number of tracked offsets.</returns>
    internal int TrackedCount(int partition)
    {
        if (!_partitions.TryGetValue(partition, out PartitionState? state))
        {
            return 0;
        }

        lock (state)
        {
            return state.Pending.Count;
        }
    }

    // Must be called under lock (state).
    private static bool HoldLocked(PartitionState state, long offset)
    {
        if (state.HeldAt is { } heldAt && offset >= heldAt)
        {
            return false;
        }

        bool first = state.HeldAt is null;
        state.HeldAt = offset;

        // Rebuild the queue in place: keep offsets below the hold, forget the rest.
        int count = state.Pending.Count;
        for (int i = 0; i < count; i++)
        {
            long pending = state.Pending.Dequeue();
            if (pending < offset)
            {
                state.Pending.Enqueue(pending);
            }
            else
            {
                state.CompletedOutOfOrder.Remove(pending);
            }
        }

        return first;
    }

    private sealed class PartitionState
    {
        // Delivered offsets in delivery order (strictly ascending within an assignment).
        internal Queue<long> Pending { get; } = new();

        // Forward-settled offsets that are not at the head of Pending (always a subset of Pending).
        internal HashSet<long> CompletedOutOfOrder { get; } = [];

        internal long? HeldAt { get; set; }

        internal long HighestTracked { get; set; } = -1;

        internal long LastStored { get; set; } = -1;

        internal bool Revoked { get; set; }
    }
}
