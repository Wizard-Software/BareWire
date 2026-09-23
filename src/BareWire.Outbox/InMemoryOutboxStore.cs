using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Transport;

namespace BareWire.Outbox;

/// <summary>
/// An in-process, non-durable outbox store backed by a concurrent queue of pending entries and a
/// concurrent dictionary of all known entries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nack deferral and escalation.</b> A transport nack does not make its entry claimable again
/// immediately. Releasing a nacked entry computes a "not before" instant from the store's
/// nack-deferral schedule, escalating with every additional nack on the same entry, and stamps it
/// on the entry together with an incremented nack counter. Releasing an entry through an ordering
/// barrier, by contrast, clears the deferral and rejoins the immediately-claimable class without
/// touching the nack counter — a barrier release means the transport never rejected the entry. A
/// released entry always rejoins the queue at the tail; when both a nack and a barrier release
/// target the same entry in one call, the nack wins.
/// </para>
/// <para>
/// <b>Bounded sweep, no spin.</b> Fetching pending entries dequeues at most as many entries as
/// were queued when the sweep began (a snapshot of the queue length taken up front), so a queue
/// full of deferred entries returns an empty batch in one bounded pass instead of spinning.
/// Every entry dequeued in the sweep that is not selected for the returned batch — because it is
/// not yet due, because it is blocked behind the head of its ordering key, or because it lost the
/// batch-size split below — is re-queued at the tail, in the order it was dequeued.
/// </para>
/// <para>
/// <b>Class split.</b> Due candidates are split into a "new" class (never nacked, or most
/// recently released by a barrier) ordered by id, and a "retry" class (due nacked entries)
/// ordered by their deferred instant then by id. For a batch of two or more slots, a quarter of
/// the batch (at least one slot) is reserved for due retries and the rest for new entries; a class
/// with fewer candidates than its share leaves the unused slots to the other class. A backlog of
/// repeatedly nacked entries therefore never starves new entries, and new entries never starve
/// due retries. For a batch of exactly one, the two classes alternate across calls whenever both
/// have candidates.
/// </para>
/// <para>
/// This store is for development and testing only, not production use. It is driven by a single
/// consumer: fetching pending entries, releasing a lock, and marking entries delivered are not
/// safe to call concurrently with each other, though saving new messages and cleaning up
/// delivered ones may run concurrently with all of them. Because a full sweep runs concurrently
/// with saves, the store's pending-message capacity check is approximate, not exact, while a
/// sweep is in flight.
/// </para>
/// <para>
/// <b>Buffer ownership.</b> The store owns the pooled body buffer of every entry it holds and
/// returns it to the shared array pool exactly once — when a delivered entry is cleaned up, or
/// when the store is disposed. Fetching pending entries never hands out a store-owned buffer:
/// each returned entry is a copy carrying its own freshly rented buffer, which the caller owns
/// and returns. Releasing a lock therefore never retains a caller's buffer and always reports an
/// empty retained set, as the EF Core store does.
/// </para>
/// <para>
/// <b>Entries orphaned by an exception or cancellation.</b> An entry claimed while fetching
/// pending entries and never subsequently released or marked delivered — for example because
/// sending its batch failed, or because a caller cancelled before releasing it or marking it
/// delivered — is never handed out again by this store instance, because the store cannot tell
/// whether it reached the transport. Its store-owned buffer is still returned once, on dispose.
/// Under per-key ordering, such an orphaned entry permanently blocks its key: every later entry
/// for that key stays queued behind it, and that backlog continues to count toward the store's
/// pending-message capacity.
/// </para>
/// </remarks>
internal sealed class InMemoryOutboxStore : IOutboxStore, IAsyncDisposable
{
    private readonly int _maxPendingMessages;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IOutboxJitterSource _jitterSource;
    private readonly OutboxNackDeferralSchedule _nackSchedule;
    private readonly ConcurrentQueue<OutboxEntry> _pending = new();
    private readonly ConcurrentDictionary<long, OutboxEntry> _all = new();
    private long _nextId;
    private bool _singleSlotRetryTurn;
    private bool _disposed;

    internal InMemoryOutboxStore(
        OutboxOptions? options = null,
        int maxPendingMessages = 10_000,
        TimeProvider? timeProvider = null,
        IOutboxJitterSource? jitterSource = null)
    {
        if (maxPendingMessages <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPendingMessages),
                maxPendingMessages,
                "maxPendingMessages must be greater than zero.");
        }

        _options = options ?? OutboxOptions.Default;
        _maxPendingMessages = maxPendingMessages;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jitterSource = jitterSource ?? SharedRandomOutboxJitterSource.Instance;

        // Created eagerly (not lazily on the first nack) so invalid options fail fast at
        // construction rather than surfacing as an exception deep inside ReleaseLockAsync, which
        // would leave the entries being released orphaned outside _pending.
        _nackSchedule = OutboxNackDeferralSchedule.FromOptions(_options, _jitterSource);
    }

    // The clock every time-dependent operation of this store reads — exactly once per operation.
    internal TimeProvider TimeProvider => _timeProvider;

    // Randomness source for retry jitter.
    internal IOutboxJitterSource JitterSource => _jitterSource;

    public ValueTask SaveMessagesAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (messages is null || messages.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        int currentPending = _pending.Count;
        if (currentPending + messages.Count > _maxPendingMessages)
        {
            throw new InvalidOperationException(
                $"Outbox store is at capacity ({_maxPendingMessages} pending messages). " +
                "Increase maxPendingMessages or ensure the outbox dispatcher is running.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        foreach (OutboundMessage message in messages)
        {
            long id = Interlocked.Increment(ref _nextId);

            // Copy ReadOnlyMemory<byte> to a pooled buffer to outlive the original allocation.
            ReadOnlyMemory<byte> originalBody = message.Body;
            byte[] pooledBody = ArrayPool<byte>.Shared.Rent(originalBody.Length);
            originalBody.Span.CopyTo(pooledBody);

            var entry = new OutboxEntry
            {
                Id = id,
                RoutingKey = message.RoutingKey,
                Headers = message.Headers,
                PooledBody = pooledBody,
                BodyLength = originalBody.Length,
                ContentType = message.ContentType,
                CreatedAt = now,
                Status = OutboxEntryStatus.Pending,
                OrderingKey = ResolveOrderingKey(message.Headers)
            };

            _all[id] = entry;
            _pending.Enqueue(entry);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<OutboxEntry>> GetPendingAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        // The cancellation token is checked exactly once, on entry. Nothing below this point may
        // throw: every entry dequeued from _pending during the sweep is tracked in `swept` and is
        // guaranteed to be re-queued by the finally block unless it was selected for the batch —
        // an exception partway through the sweep must never strand a dequeued entry outside
        // _pending.
        cancellationToken.ThrowIfCancellationRequested();

        if (batchSize <= 0)
        {
            return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>([]);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool perKey = _options.OrderingMode == OrderingMode.PerKey;

        // PerKey: a keyed entry is claimable only when it is the head (lowest id) of its key
        // group among all undelivered entries — including any entry currently deferred by a
        // nack, so a deferred head still blocks its key. Keyless entries always pass through.
        // This is an O(|_all|) scan, same cost as before this change.
        Dictionary<string, long>? headIdPerKey = null;
        if (perKey)
        {
            headIdPerKey = [];
            foreach (OutboxEntry candidate in _all.Values)
            {
                if (candidate.Status != OutboxEntryStatus.Pending || candidate.OrderingKey is null)
                {
                    continue;
                }

                if (!headIdPerKey.TryGetValue(candidate.OrderingKey, out long currentHead) || candidate.Id < currentHead)
                {
                    headIdPerKey[candidate.OrderingKey] = candidate.Id;
                }
            }
        }

        // Bounded sweep: a snapshot of the queue length taken up front caps the number of
        // dequeues, so a queue full of not-yet-due entries returns an empty batch instead of
        // spinning.
        int sweepCount = _pending.Count;
        var swept = new List<OutboxEntry>(sweepCount);
        var setAside = new List<OutboxEntry>();
        var fresh = new List<OutboxEntry>();
        var retries = new List<OutboxEntry>();
        var seenIds = new HashSet<long>();
        int newTake = 0;
        int retryTake = 0;

        try
        {
            for (int i = 0; i < sweepCount && _pending.TryDequeue(out OutboxEntry? entry); i++)
            {
                swept.Add(entry);

                if (entry.Status != OutboxEntryStatus.Pending || !seenIds.Add(entry.Id))
                {
                    // Already delivered elsewhere, or a duplicate id already seen in this sweep
                    // (a caller releasing an id it never claimed could otherwise queue the same
                    // entry twice) — drop, do not re-queue.
                    continue;
                }

                if (perKey && entry.OrderingKey is not null
                    && (!headIdPerKey!.TryGetValue(entry.OrderingKey, out long headId) || entry.Id != headId))
                {
                    setAside.Add(entry);
                    continue;
                }

                if (entry.NotBefore is { } notBefore && notBefore >= now)
                {
                    setAside.Add(entry);
                    continue;
                }

                (entry.NotBefore is null ? fresh : retries).Add(entry);
            }

            fresh.Sort(static (a, b) => a.Id.CompareTo(b.Id));
            retries.Sort(static (a, b) =>
            {
                int byNotBefore = a.NotBefore!.Value.CompareTo(b.NotBefore!.Value);
                return byNotBefore != 0 ? byNotBefore : a.Id.CompareTo(b.Id);
            });

            (newTake, retryTake) = SplitBatch(batchSize, fresh.Count, retries.Count);
        }
        finally
        {
            // Every dequeued entry not selected for the batch — set aside, or a candidate that
            // lost the split — goes back to the tail, in the order it was dequeued. Selected
            // entries and dropped entries (non-Pending or a duplicate id) are the only ones
            // excluded.
            HashSet<OutboxEntry> requeue = new(setAside);
            for (int j = newTake; j < fresh.Count; j++)
            {
                requeue.Add(fresh[j]);
            }

            for (int j = retryTake; j < retries.Count; j++)
            {
                requeue.Add(retries[j]);
            }

            // Remove (not just Contains) so an entry dequeued twice in one sweep — a duplicate queue
            // slot — is re-queued once, not twice.
            foreach (OutboxEntry entry in swept)
            {
                if (requeue.Remove(entry))
                {
                    _pending.Enqueue(entry);
                }
            }
        }

        var result = new List<OutboxEntry>(newTake + retryTake);
        for (int j = 0; j < newTake; j++)
        {
            result.Add(CopyForDispatch(fresh[j]));
        }

        for (int j = 0; j < retryTake; j++)
        {
            result.Add(CopyForDispatch(retries[j]));
        }

        return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(result);
    }

    // Splits a batch of batchSize slots between the "new" and due "retry" classes. For
    // batchSize >= 2, at least GetRetryReserve(batchSize) slots go to due retries whenever any
    // are waiting. For batchSize == 1, the two classes alternate across calls (via
    // _singleSlotRetryTurn) whenever both have candidates in the same cycle; the first contested
    // cycle serves the "new" class.
    private (int NewTake, int RetryTake) SplitBatch(int batchSize, int freshCount, int retryCount)
    {
        if (batchSize == 1)
        {
            if (freshCount > 0 && retryCount > 0)
            {
                bool retryTurn = _singleSlotRetryTurn;
                _singleSlotRetryTurn = !_singleSlotRetryTurn;
                return retryTurn ? (0, 1) : (1, 0);
            }

            return freshCount > 0 ? (1, 0) : retryCount > 0 ? (0, 1) : (0, 0);
        }

        int reserve = GetRetryReserve(batchSize);
        int retryTake = Math.Min(retryCount, batchSize - Math.Min(freshCount, batchSize - reserve));
        int newTake = Math.Min(freshCount, batchSize - retryTake);
        return (newTake, retryTake);
    }

    // Number of batch slots reserved for due retries in a batch of batchSize (>= 2).
    private static int GetRetryReserve(int batchSize) => Math.Max(1, batchSize / 4);

    // Saturating now + deferral: returns DateTimeOffset.MaxValue instead of overflowing when now
    // is already within `deferral` of the maximum representable instant. `now` is normalized to UTC
    // first: DateTimeOffset addition also range-checks the local clock time, so a positive offset
    // could otherwise throw past the UTC-based guard.
    private static DateTimeOffset AddSaturating(DateTimeOffset now, TimeSpan deferral)
    {
        DateTimeOffset utcNow = now.ToUniversalTime();
        return deferral >= DateTimeOffset.MaxValue - utcNow ? DateTimeOffset.MaxValue : utcNow + deferral;
    }

    public ValueTask MarkDeliveredAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = _timeProvider.GetUtcNow();

        foreach (long id in ids)
        {
            if (_all.TryGetValue(id, out OutboxEntry? entry))
            {
                entry.Status = OutboxEntryStatus.Delivered;
                entry.DeliveredAt = now;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => ReleaseLockAsync(ids, [], cancellationToken);

    public ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
        IReadOnlyList<long> nackedIds,
        IReadOnlyList<long> barrierReleasedIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(nackedIds);
        ArgumentNullException.ThrowIfNull(barrierReleasedIds);

        if (nackedIds.Count == 0 && barrierReleasedIds.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlySet<long>>(FrozenSet<long>.Empty);
        }

        // This store has no lock column — "release" means re-enqueue so the entry is dispatched
        // again on the next poll. A nack defers the entry through the nack-deferral schedule and
        // increments its nack counter; an ordering-barrier release clears the deferral (the entry
        // rejoins the "new" class) and leaves the counter untouched. The caller only ever held a
        // copy of each entry (see CopyForDispatch), so no caller buffer is retained and the returned
        // set is always empty. Delivered or unknown ids are skipped (idempotent). An id present in
        // both lists is treated as a nack only — the nacked list is processed first and claims the
        // id via `released`, so the barrier pass below becomes a no-op for it.
        //
        // Invariant: for every entry, NotBefore/NackCount are computed and assigned before that
        // entry's Enqueue call, and the Enqueue call is the last thing done for that entry.
        DateTimeOffset now = _timeProvider.GetUtcNow();
        OutboxNackDeferralPlan? plan = nackedIds.Count > 0 ? _nackSchedule.CreatePlan() : null;
        HashSet<long>? released = null;

        foreach (long id in nackedIds)
        {
            if (_all.TryGetValue(id, out OutboxEntry? entry)
                && entry.Status == OutboxEntryStatus.Pending
                && (released ??= []).Add(id))
            {
                TimeSpan deferral = plan!.GetDeferralForRow(entry.NackCount, entry.Id);
                entry.NotBefore = AddSaturating(now, deferral);
                entry.NackCount = entry.NackCount == int.MaxValue ? int.MaxValue : entry.NackCount + 1;
                _pending.Enqueue(entry);
            }
        }

        foreach (long id in barrierReleasedIds)
        {
            if (_all.TryGetValue(id, out OutboxEntry? entry)
                && entry.Status == OutboxEntryStatus.Pending
                && (released ??= []).Add(id))
            {
                entry.NotBefore = null;
                _pending.Enqueue(entry);
            }
        }

        return ValueTask.FromResult<IReadOnlySet<long>>(FrozenSet<long>.Empty);
    }

    public ValueTask CleanupAsync(
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - retention;

        foreach ((long id, OutboxEntry entry) in _all)
        {
            if (entry.Status == OutboxEntryStatus.Delivered
                && entry.DeliveredAt.HasValue
                && entry.DeliveredAt.Value <= cutoff)
            {
                if (_all.TryRemove(id, out _))
                {
                    ReturnPooledBody(entry);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        // Drain the pending queue and return all pooled buffers.
        while (_pending.TryDequeue(out _))
        {
            // Entries are also in _all — return buffers via _all below.
        }

        foreach ((long _, OutboxEntry entry) in _all)
        {
            ReturnPooledBody(entry);
        }

        _all.Clear();

        return ValueTask.CompletedTask;
    }

    private static void ReturnPooledBody(OutboxEntry entry)
    {
        ArrayPool<byte>.Shared.Return(entry.PooledBody);
    }

    // Hands the caller a copy of a claimed entry carrying its own freshly rented body buffer, so the
    // caller can return that buffer to the pool on any path without touching the store-owned one.
    private static OutboxEntry CopyForDispatch(OutboxEntry entry)
    {
        byte[] body = ArrayPool<byte>.Shared.Rent(entry.BodyLength);
        entry.PooledBody.AsSpan(0, entry.BodyLength).CopyTo(body);

        return new OutboxEntry
        {
            Id = entry.Id,
            RoutingKey = entry.RoutingKey,
            Headers = entry.Headers,
            PooledBody = body,
            BodyLength = entry.BodyLength,
            ContentType = entry.ContentType,
            CreatedAt = entry.CreatedAt,
            DeliveredAt = entry.DeliveredAt,
            Status = entry.Status,
            OrderingKey = entry.OrderingKey,
            NotBefore = entry.NotBefore,
            NackCount = entry.NackCount,
        };
    }

    /// <summary>
    /// Returns the store-owned entry for <paramref name="id"/>, or <see langword="null"/> when the
    /// store no longer holds it. A test hook: fetched batches carry copies, so observing the state a
    /// release or delivery left behind needs the store's own instance.
    /// </summary>
    internal OutboxEntry? FindEntry(long id) => _all.TryGetValue(id, out OutboxEntry? entry) ? entry : null;

    // Promotes the ordering key from the message headers when PerKey mode is active.
    // Rules (parity with EfCoreOutboxStore):
    //   - Only active when OrderingMode == PerKey.
    //   - Key must be present, non-whitespace, and <= 256 characters.
    //   - Keys longer than 256 characters produce null (keyless) — NEVER truncated,
    //     to avoid collapsing distinct long keys and creating a head-of-line collision vector.
    private string? ResolveOrderingKey(IReadOnlyDictionary<string, string> headers)
    {
        if (_options.OrderingMode != OrderingMode.PerKey)
        {
            return null;
        }

        string? headerName = _options.OrderingKeyHeaderName;
        if (string.IsNullOrEmpty(headerName))
        {
            return null;
        }

        if (!headers.TryGetValue(headerName, out string? key))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        // Over-limit keys become keyless (passthrough). Never truncate — see comment above.
        if (key.Length > 256)
        {
            return null;
        }

        return key;
    }
}
