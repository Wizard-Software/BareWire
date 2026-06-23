using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using BareWire.Abstractions.Transport;

namespace BareWire.Outbox;

internal sealed class InMemoryOutboxStore : IOutboxStore, IAsyncDisposable
{
    private readonly int _maxPendingMessages;
    private readonly ConcurrentQueue<OutboxEntry> _pending = new();
    private readonly ConcurrentDictionary<long, OutboxEntry> _all = new();
    private long _nextId;
    private bool _disposed;

    internal InMemoryOutboxStore(int maxPendingMessages = 10_000)
    {
        if (maxPendingMessages <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPendingMessages),
                maxPendingMessages,
                "maxPendingMessages must be greater than zero.");
        }

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
                Status = OutboxEntryStatus.Pending
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
}
