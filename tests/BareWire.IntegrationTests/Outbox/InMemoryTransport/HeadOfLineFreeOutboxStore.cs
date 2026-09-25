using BareWire.Abstractions.Transport;
using BareWire.Outbox;

namespace BareWire.IntegrationTests.Outbox.InMemoryTransport;

/// <summary>
/// Wraps an <see cref="InMemoryOutboxStore"/> so every entry <c>GetPendingAsync</c> returns carries an
/// <c>OrderingKey</c> promoted from a message header, even though the wrapped store itself runs in
/// <c>OrderingMode.None</c> and therefore performs no head-of-line filtering of its own.
/// </summary>
/// <remarks>
/// Emulates the documented "degraded ordering" mode: a store/dialect without native head-of-line
/// support, combined with <c>OutboxOptions.AllowDegradedOrdering</c>. The dispatcher still sees
/// <c>OutboxOptions.OrderingMode == PerKey</c> and still applies its own per-key barrier over the
/// entries this store hands it — only the store-side head-of-line claim predicate is absent, so a
/// confirmed sibling behind a rejected head of the same key can appear in the same claimed batch. This
/// is the only configuration in which the dispatcher's per-key ordering barrier actually releases
/// confirmed siblings; the two built-in stores always claim just the head of a key.
/// </remarks>
internal sealed class HeadOfLineFreeOutboxStore : IOutboxStore
{
    private readonly InMemoryOutboxStore _inner;
    private readonly string _orderingKeyHeaderName;

    internal HeadOfLineFreeOutboxStore(InMemoryOutboxStore inner, string orderingKeyHeaderName)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _orderingKeyHeaderName = orderingKeyHeaderName ?? throw new ArgumentNullException(nameof(orderingKeyHeaderName));
    }

    public ValueTask SaveMessagesAsync(
        IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
        => _inner.SaveMessagesAsync(messages, cancellationToken);

    public async ValueTask<IReadOnlyList<OutboxEntry>> GetPendingAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OutboxEntry> batch = await _inner.GetPendingAsync(batchSize, cancellationToken)
            .ConfigureAwait(false);

        if (batch.Count == 0)
        {
            return batch;
        }

        var promoted = new List<OutboxEntry>(batch.Count);
        foreach (OutboxEntry entry in batch)
        {
            promoted.Add(new OutboxEntry
            {
                Id = entry.Id,
                RoutingKey = entry.RoutingKey,
                Headers = entry.Headers,
                PooledBody = entry.PooledBody,
                BodyLength = entry.BodyLength,
                ContentType = entry.ContentType,
                CreatedAt = entry.CreatedAt,
                DeliveredAt = entry.DeliveredAt,
                Status = entry.Status,
                NotBefore = entry.NotBefore,
                NackCount = entry.NackCount,
                OrderingKey = entry.Headers.TryGetValue(_orderingKeyHeaderName, out string? key) ? key : null,
            });
        }

        return promoted;
    }

    public ValueTask MarkDeliveredAsync(
        IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
        => _inner.MarkDeliveredAsync(ids, cancellationToken);

    public ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
        IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
        => _inner.ReleaseLockAsync(ids, cancellationToken);

    public ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
        IReadOnlyList<long> nackedIds,
        IReadOnlyList<long> barrierReleasedIds,
        CancellationToken cancellationToken = default)
        // The retained-buffer set is passed through unchanged — see DispatchCycleRecorder's Decorator
        // for the same rule and its rationale.
        => _inner.ReleaseLockAsync(nackedIds, barrierReleasedIds, cancellationToken);

    public ValueTask CleanupAsync(TimeSpan retention, CancellationToken cancellationToken = default)
        => _inner.CleanupAsync(retention, cancellationToken);
}
