namespace BareWire.Outbox;

/// <summary>
/// Optional outbox store capability that reports the due instant of the oldest row waiting for a
/// deferred retry — the class of rows a transport rejected, or whose claim was abandoned, and whose
/// deferral has already elapsed while no dispatcher instance has re-claimed the row yet.
/// </summary>
/// <remarks>
/// Implemented by stores that can answer this question without an unbounded scan on every dispatch
/// cycle. <see cref="OutboxDispatcher"/> queries it at most once per metric-collection interval — see
/// <see cref="OutboxRetryDiagnostics.TryConsumeAgeSampleRequest"/> — so a store that never implements
/// this interface simply never pays for the query, and the rate-limited retry log keeps working either
/// way.
/// </remarks>
internal interface IOutboxRetryBacklogProbe
{
    /// <summary>
    /// Returns the due instant of the oldest row whose deferred retry has elapsed and that is not yet
    /// claimed by any dispatcher instance, or <see langword="null"/> when no such row exists.
    /// </summary>
    /// <param name="now">The instant to evaluate "due" against.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    ValueTask<DateTimeOffset?> GetOldestDueRetryAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
