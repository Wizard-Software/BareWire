namespace BareWire.Outbox.EntityFramework.Internal;

// Pure batch arithmetic of the fair two-class claim. A claim cycle splits its effective capacity
// (the batch size minus the rows this instance already owns) between two classes of rows:
//   - "new" rows (never claimed, LockedAt IS NULL), claimed in Id order with a reservation of
//     effective - retryReserve slots, so any number of due retries cannot starve fresh messages;
//   - "due retries" (deferred nacks and abandoned claims, LockedAt older than the stale-lock cutoff),
//     claimed in due-time order with at least retryReserve slots, so fresh messages cannot starve them.
// Unused capacity of either class is handed to the other (top-up), so a cycle is work-conserving.
internal static class OutboxFairClaimPlan
{
    // Stale-lock cutoff older than any real LockedAt value. With it the claim predicate
    // (LockedAt IS NULL OR LockedAt < cutoff) admits only rows that were never claimed.
    internal static readonly DateTimeOffset NewRowsOnlyCutoff = DateTimeOffset.UnixEpoch;

    // Capacity left for new claims once the rows this instance still owns (carry-forward rows, e.g. a
    // batch whose send failed and was never released) are counted against the batch size. Bounds the
    // number of rows one instance holds under a valid lock to batchSize.
    internal static int GetEffectiveBatchSize(int batchSize, int carryForwardCount)
    {
        if (batchSize <= 0)
        {
            return 0;
        }

        int carried = Math.Clamp(carryForwardCount, 0, batchSize);
        return batchSize - carried;
    }

    // Slots reserved for due retries: max(1, effective / 4) for an effective batch of two or more.
    // A single-slot batch cannot split, so the whole slot goes to the class whose turn it is.
    internal static int GetRetryReserve(int effectiveBatchSize, bool singleSlotRetryTurn)
    {
        if (effectiveBatchSize <= 0)
        {
            return 0;
        }

        if (effectiveBatchSize == 1)
        {
            return singleSlotRetryTurn ? 1 : 0;
        }

        return Math.Max(1, effectiveBatchSize / 4);
    }

    // Normalizes the affected-row count a claim statement reports. A negative count (for example a
    // provider running with SET NOCOUNT ON) is treated as the full limit, which only lowers the limits
    // of the following steps of the same cycle; a count above the limit is capped at the limit.
    internal static int ClampClaimed(int reportedCount, int limit)
    {
        if (limit <= 0)
        {
            return 0;
        }

        return reportedCount < 0 ? limit : Math.Min(reportedCount, limit);
    }

    // The top-up step runs only when the new-rows step filled its whole reservation (otherwise no
    // claimable new row is left) and the retry step left part of the batch empty.
    internal static bool ShouldTopUp(int effectiveBatchSize, int retryReserve, int newClaimed, int retryClaimed)
        => effectiveBatchSize > 0
            && newClaimed == effectiveBatchSize - retryReserve
            && newClaimed + retryClaimed < effectiveBatchSize;

    // Client-side split of known candidate counts: new rows up to the reservation, due retries in the
    // remaining capacity, then the top-up with further new rows. NewTake includes the top-up.
    internal static (int NewTake, int RetryTake) SplitCandidates(
        int effectiveBatchSize,
        int retryReserve,
        int newCount,
        int retryCount)
    {
        if (effectiveBatchSize <= 0)
        {
            return (0, 0);
        }

        int newReservation = Math.Max(0, effectiveBatchSize - retryReserve);
        int newTake = Math.Min(Math.Max(0, newCount), newReservation);
        int retryTake = Math.Min(Math.Max(0, retryCount), effectiveBatchSize - newTake);

        if (ShouldTopUp(effectiveBatchSize, retryReserve, newTake, retryTake))
        {
            newTake += Math.Min(newCount - newTake, effectiveBatchSize - newTake - retryTake);
        }

        return (newTake, retryTake);
    }
}
