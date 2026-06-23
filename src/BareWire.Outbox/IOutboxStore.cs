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
    /// Explicitly releases the per-instance claim on the given rows (zeroes the lock) so they are
    /// re-claimed on the next poll cycle (~<c>PollingInterval</c>) instead of waiting for the
    /// <c>OutboxLockTimeout</c> to expire. Called by the dispatcher for broker-nacked ids to cut
    /// retry latency.
    /// </summary>
    /// <param name="ids">Ids of the nacked rows to release. Only rows still claimed by this instance
    /// and not yet delivered are affected.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// The subset of <paramref name="ids"/> whose pooled body buffer the store has <em>retained</em>
    /// ownership of (e.g. re-enqueued in-memory entries that are still referenced by the store). The
    /// caller MUST NOT return those buffers to the <see cref="System.Buffers.ArrayPool{T}"/> — doing so
    /// would be a use-after-return on the next dispatch. Stores that copy each row into a fresh
    /// per-cycle buffer (EF Core) retain nothing and return an empty set.
    /// </returns>
    /// <remarks>
    /// A permanently-nacked ("poison") message is re-sent every <c>PollingInterval</c> (versus every
    /// <c>OutboxLockTimeout</c> before this method existed) until it succeeds; bounded retry
    /// (<c>MaxRetries</c> / dead-letter) is a deliberate non-goal here and a roadmap follow-up.
    /// </remarks>
    ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    ValueTask CleanupAsync(
        TimeSpan retention,
        CancellationToken cancellationToken = default);
}
