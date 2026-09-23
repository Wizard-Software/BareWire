using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Outbox;
using Xunit;

namespace BareWire.UnitTests.Outbox;

/// <summary>
/// Proves three P0 liveness properties of the outbox dispatch loop — end to end, against a real
/// <c>OutboxDispatcher</c> and a real store (EF Core on SQLite, and <c>InMemoryOutboxStore</c>):
/// a poison retry cohort never starves fresh rows, the retry class itself has bounded liveness against
/// a poison cohort with lower ids, and the <c>PerKey</c> ordering barrier blocks only the newer rows of
/// its own key while every other row keeps progressing.
/// </summary>
public sealed class OutboxP0LivenessTests
{
    private static readonly TimeSpan Polling = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(10);
    private const int Batch = 4; // retry reserve R = max(1, Batch / 4) = 1
    private const int NewShare = Batch - 1; // 3

    private static OutboxOptions Options(OrderingMode mode = OrderingMode.None) => new()
    {
        DispatchBatchSize = Batch,
        PollingInterval = Polling,
        OutboxLockTimeout = TimeSpan.FromMilliseconds(160),
        OrderingMode = mode,
        OrderingKeyHeaderName = mode == OrderingMode.PerKey ? "x-ordering-key" : null,
    };

    private static string[] Labels(string prefix, int count)
        => [.. Enumerable.Range(1, count).Select(i => $"{prefix}-{i}")];

    // ── (1) No starvation of new rows by an older poison retry cohort ──────────

    [Theory]
    [InlineData(OutboxP0StoreKind.EfCoreSqlite)]
    [InlineData(OutboxP0StoreKind.InMemory)]
    public async Task Dispatch_PoisonRetryCohortOlderThanNewRows_DeliversEveryNewRowWithinBoundWithoutPause(
        OutboxP0StoreKind kind)
    {
        string[] poison = Labels("poison", 2 * Batch); // >= 2 x DispatchBatchSize, lower ids than the new rows
        string[] fresh = Labels("new", 15);
        await using OutboxP0Harness h = await OutboxP0Harness.CreateAsync(
            kind, Options(), Step, isConfirmed: (label, _) => !label.StartsWith("poison", StringComparison.Ordinal));
        await h.SaveAsync(poison);
        await h.SeedAsDueRetriesAsync(poison); // already-nacked due retries, RetryCount = 1
        await h.SaveAsync(fresh);

        await h.RunUntilAsync(
            x => fresh.All(l => x.DeliveredInCycle(l) is not null) && poison.Any(l => x.SendsOf(l).Count >= 3),
            maxCycles: 200);

        int lastNewCycle = fresh.Max(l => h.DeliveredInCycle(l)!.Value);
        // The new-rows share of a cycle is NewShare = Batch - R, so all 15 new rows are through within
        // ceil(15 / 3) = 5 cycles even under a permanently rejected poison cohort.
        lastNewCycle.Should().BeLessThanOrEqualTo((fresh.Length + NewShare - 1) / NewShare);
        h.Events.OfType<Paused>().Where(p => p.Cycle <= lastNewCycle).Should().BeEmpty(
            "a minority of nacks must not pause the drain before the new rows are through");

        // Deferred with escalation: the gap between consecutive sends of a poison row is at least its
        // base deferral, PollingInterval x 2^(RetryCount before that nack), capped at OutboxLockTimeout.
        // The seeded RetryCount is 1, so the first observed gap is bucket 1 (2 x Polling) and the next,
        // after a further nack, is bucket 2 (4 x Polling).
        foreach (string label in poison.Where(l => h.SendsOf(l).Count >= 3))
        {
            var sends = h.SendsOf(label).Cast<Sent>().ToArray();
            (sends[1].Now - sends[0].Now).Should().BeGreaterThanOrEqualTo(2 * Polling);
            (sends[2].Now - sends[1].Now).Should().BeGreaterThanOrEqualTo(4 * Polling);
        }

        // Visibility: the rate-limited Warning names a poison row, and the counter equals every poison
        // nack the transport actually sent.
        int poisonNacks = h.Events.OfType<Sent>().Count(s => !s.Confirmed);
        h.Events.OfType<RetryWarning>().Should().NotBeEmpty();
        h.RetriedRowsCounterTotal.Should().Be(poisonNacks);
    }

    // ── (2) Retry-class liveness against a poison cohort with lower ids ────────

    [Theory]
    [InlineData(OutboxP0StoreKind.EfCoreSqlite)]
    [InlineData(OutboxP0StoreKind.InMemory)]
    public async Task Dispatch_PoisonCohortWithLowerIdsThanOnceRejectedRows_RedeliversEveryOnceRejectedRowWithinBound(
        OutboxP0StoreKind kind)
    {
        // S >> C_r x B_max: 96 poison rows against a retry capacity of at most 4 per cycle; the steady-state
        // due rate of the capped poison cohort exceeds the batch, so an id-ordered retry claim would
        // starve the once-rejected rows indefinitely.
        string[] poison = Labels("poison", 96);
        string[] once = Labels("once", 4);
        await using OutboxP0Harness h = await OutboxP0Harness.CreateAsync(
            kind, Options(), Step,
            isConfirmed: (label, attempt) => label.StartsWith("once", StringComparison.Ordinal) && attempt >= 2);
        await h.SaveAsync(poison);
        await h.SeedAsDueRetriesAsync(poison); // lower ids, already due
        await h.SaveAsync(once);               // higher ids, rejected exactly once

        // The drain term divides by the whole batch, not by the retry reserve: once the first attempts of
        // the once-rejected rows are over, the new-rows class is empty, so the top-up step hands the retry
        // class the full batch capacity every cycle. That makes this bound tighter than one derived from
        // the reserve alone.
        int firstAttemptCycles = (once.Length + NewShare - 1) / NewShare;                    // 2
        int deferralCycles = (int)Math.Ceiling(1.2 * Polling.Ticks / Step.Ticks) + 1;         // 4
        int drainCycles = (poison.Length + once.Length + Batch - 1) / Batch;                  // 25
        int bound = firstAttemptCycles + deferralCycles + drainCycles;                        // 31

        await h.RunUntilAsync(x => once.All(l => x.DeliveredInCycle(l) is not null), maxCycles: 3 * bound);

        foreach (string label in once)
        {
            h.SendsOf(label).Cast<Sent>().Select(s => s.Confirmed).Should().Equal(false, true);
            h.DeliveredInCycle(label).Should().NotBeNull().And.BeLessThanOrEqualTo(bound);
        }
    }

    // ── (3) PerKey ordering barrier: a deferred head blocks only its own key ───

    [Theory]
    [InlineData(OutboxP0StoreKind.EfCoreSqlite)]
    [InlineData(OutboxP0StoreKind.InMemory)]
    public async Task Dispatch_PerKeyHeadDeferred_BlocksNewerRowsOfItsKeyWhileOtherRowsProgress(OutboxP0StoreKind kind)
    {
        string[] keyA = Labels("a", 3); // a-1 is the head, rejected on attempts 1 and 2
        string[] keyB = Labels("b", 3);
        string[] keyless = Labels("free", 3);
        await using OutboxP0Harness h = await OutboxP0Harness.CreateAsync(
            kind, Options(OrderingMode.PerKey), Step,
            isConfirmed: (label, attempt) => label != keyA[0] || attempt >= 3);
        await h.SaveAsync(keyA, orderingKey: "A");
        await h.SaveAsync(keyB, orderingKey: "B");
        await h.SaveAsync(keyless);

        string[] all = [.. keyA, .. keyB, .. keyless];
        await h.RunUntilAsync(x => all.All(l => x.DeliveredInCycle(l) is not null), maxCycles: 200);

        int headDelivered = h.DeliveredInCycle(keyA[0])!.Value;
        h.SendsOf(keyA[0]).Should().HaveCount(3, "the head was deferred twice before it was confirmed");
        // Newer rows of the key are not even sent while the head is pending (store head filter + barrier).
        h.SendsOf(keyA[1]).Cast<Sent>().Min(s => s.Cycle).Should().BeGreaterThan(headDelivered);
        h.SendsOf(keyA[2]).Cast<Sent>().Min(s => s.Cycle).Should().BeGreaterThan(h.DeliveredInCycle(keyA[1])!.Value);
        // Isolation: every row of another key and every keyless row is delivered while the head is deferred.
        foreach (string label in keyB.Concat(keyless))
        {
            h.DeliveredInCycle(label)!.Value.Should().BeLessThan(headDelivered);
        }

        // Per-key delivery order follows id order.
        keyB.Select(l => h.DeliveredInCycle(l)!.Value).Should().BeInAscendingOrder();
    }
}
