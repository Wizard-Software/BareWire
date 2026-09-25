using BareWire.Abstractions.Transport;

namespace BareWire.Outbox;

internal interface IOutboxStore
{
    ValueTask SaveMessagesAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<OutboxEntry>> GetPendingAsync(
        int batchSize,
        CancellationToken cancellationToken = default);

    ValueTask MarkDeliveredAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Shorthand for
    /// <see cref="ReleaseLockAsync(IReadOnlyList{long}, IReadOnlyList{long}, CancellationToken)"/> that
    /// releases every id as a transport nack and passes no ordering-barrier ids.
    /// </summary>
    /// <param name="ids">Ids of the rows the transport rejected. Only rows still claimed by this
    /// instance and not yet delivered are affected.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// The subset of <paramref name="ids"/> whose pooled body buffer the store has <em>retained</em>
    /// ownership of — see the full overload for the buffer-ownership contract.
    /// </returns>
    ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly releases this instance's claim on two kinds of rows, so neither waits for
    /// <c>OutboxLockTimeout</c> to expire:
    /// <list type="bullet">
    /// <item><description><paramref name="nackedIds"/> — rows the transport rejected. Each is deferred by
    /// the nack backoff schedule (not claimable again before its deferral has elapsed) and its
    /// <c>RetryCount</c> is incremented.</description></item>
    /// <item><description><paramref name="barrierReleasedIds"/> — rows the transport never rejected but
    /// that were held back only by the per-key ordering barrier. They are released immediately, without
    /// a deferral and with <c>RetryCount</c> unchanged.</description></item>
    /// </list>
    /// An id present in both lists is treated as nacked. Only rows still claimed by this instance and
    /// not yet delivered are affected.
    /// </summary>
    /// <param name="nackedIds">Ids of the rows the transport rejected.</param>
    /// <param name="barrierReleasedIds">Ids of the rows released only because of the ordering barrier.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// The subset of the given ids whose pooled body buffer the store has <em>retained</em> ownership of
    /// (e.g. re-enqueued in-memory entries that are still referenced by the store). The caller MUST NOT
    /// return those buffers to the <see cref="System.Buffers.ArrayPool{T}"/> — doing so would be a
    /// use-after-return on the next dispatch. Stores that copy each row into a fresh per-cycle buffer
    /// (EF Core) retain nothing and return an empty set.
    /// </returns>
    /// <remarks>
    /// The nack deferral starts at <c>PollingInterval</c> and doubles with every further rejection of
    /// the same row, capped at <c>OutboxLockTimeout</c> (plus per-row jitter), so a permanently rejected
    /// ("poison") row is retried at most about once per <c>OutboxLockTimeout</c> instead of every poll
    /// cycle. Bounded retry (<c>MaxRetries</c> / dead-letter) remains a deliberate non-goal here.
    /// </remarks>
    ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
        IReadOnlyList<long> nackedIds,
        IReadOnlyList<long> barrierReleasedIds,
        CancellationToken cancellationToken = default);

    ValueTask CleanupAsync(
        TimeSpan retention,
        CancellationToken cancellationToken = default);
}
