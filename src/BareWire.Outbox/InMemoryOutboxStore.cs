using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Transport;

namespace BareWire.Outbox;

internal sealed class InMemoryOutboxStore : IOutboxStore, IAsyncDisposable
{
    private readonly int _maxPendingMessages;
    private readonly OutboxOptions _options;
    private readonly ConcurrentQueue<OutboxEntry> _pending = new();
    private readonly ConcurrentDictionary<long, OutboxEntry> _all = new();
    private long _nextId;
    private bool _disposed;

    internal InMemoryOutboxStore(OutboxOptions? options = null, int maxPendingMessages = 10_000)
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
    }

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

        DateTimeOffset now = DateTimeOffset.UtcNow;

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
        cancellationToken.ThrowIfCancellationRequested();

        if (_options.OrderingMode != OrderingMode.PerKey)
        {
            // None: original path — no grouping, no extra allocation, bit-identical to pre-R7.7.
            var batch = new List<OutboxEntry>(Math.Min(batchSize, _pending.Count));

            while (batch.Count < batchSize && _pending.TryDequeue(out OutboxEntry? entry))
            {
                // Skip entries that were already delivered (e.g. by a concurrent dispatch call).
                if (entry.Status == OutboxEntryStatus.Pending)
                {
                    batch.Add(entry);
                }
            }

            return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(batch);
        }

        // PerKey: head-of-line enforcement per ordering key.
        // A keyed row is claimable only when there is no strictly older undelivered row with the
        // same key (i.e., the row must be the head of its key group). Keyless rows always pass
        // through. Filter applied before the batch limit.
        //
        // Implementation: O(undelivered)/cycle — scans _all to find the minimum Id per key group
        // among all undelivered entries, then dequeues from _pending and keeps only heads or
        // keyless entries. This is test/dev only — NOT production. For production use PostgreSQL
        // with the atomic FOR UPDATE SKIP LOCKED claim path in EfCoreOutboxStore.
        Dictionary<string, long> headIdPerKey = _all
            .Values
            .Where(e => e.Status == OutboxEntryStatus.Pending && e.OrderingKey is not null)
            .GroupBy(e => e.OrderingKey!)
            .ToDictionary(g => g.Key, g => g.Min(e => e.Id));

        // Drain candidates from the pending queue; re-enqueue blocked keyed rows for the next
        // cycle (they are not yet the head of their key).
        var perKeyBatch = new List<OutboxEntry>(Math.Min(batchSize, _pending.Count));
        var blocked = new List<OutboxEntry>();

        while (perKeyBatch.Count < batchSize && _pending.TryDequeue(out OutboxEntry? entry))
        {
            if (entry.Status != OutboxEntryStatus.Pending)
            {
                continue;
            }

            if (entry.OrderingKey is null)
            {
                // Keyless: always eligible.
                perKeyBatch.Add(entry);
            }
            else if (headIdPerKey.TryGetValue(entry.OrderingKey, out long headId) && entry.Id == headId)
            {
                // This entry is the head of its key group.
                perKeyBatch.Add(entry);
            }
            else
            {
                // Not the head — block and re-enqueue after this sweep.
                blocked.Add(entry);
            }
        }

        foreach (OutboxEntry blockedEntry in blocked)
        {
            _pending.Enqueue(blockedEntry);
        }

        return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(perKeyBatch);
    }

    public ValueTask MarkDeliveredAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = DateTimeOffset.UtcNow;

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
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlySet<long>>(FrozenSet<long>.Empty);
        }

        // This store has no lock column — "release" means re-enqueue so the entry is dispatched
        // again on the next poll. The entry instance is still referenced from _all, so re-enqueuing
        // keeps its pooled buffer alive; the returned set tells the dispatcher NOT to return those
        // buffers to the ArrayPool. Delivered or unknown ids are skipped (idempotent). In the
        // dispatcher flow each id was removed from _pending by GetPendingAsync, so re-enqueue is 1:1.
        HashSet<long>? retained = null;

        foreach (long id in ids)
        {
            if (_all.TryGetValue(id, out OutboxEntry? entry) && entry.Status == OutboxEntryStatus.Pending)
            {
                _pending.Enqueue(entry);
                (retained ??= []).Add(id);
            }
        }

        return ValueTask.FromResult<IReadOnlySet<long>>(retained ?? (IReadOnlySet<long>)FrozenSet<long>.Empty);
    }

    public ValueTask CleanupAsync(
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - retention;

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

    // Promotes the ordering key from the message headers when PerKey mode is active.
    // Rules (SEC-2 / §2.4 of the R7.7 plan — parity with EfCoreOutboxStore):
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
