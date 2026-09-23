using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Outbox;

public sealed class InMemoryOutboxStoreNackDeferralTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly OutboxOptions Options = new()
        { PollingInterval = TimeSpan.FromSeconds(1), OutboxLockTimeout = TimeSpan.FromSeconds(30) };

    // Deterministic jitter: every draw returns 0 -> bucket j gets 0.05 * j of the base deferral.
    private sealed class ZeroJitter : IOutboxJitterSource { public double NextDouble() => 0.0; }

    // Expected deferral computed through the same internal schedule the store must use.
    private static TimeSpan ExpectedDeferral(OutboxOptions options, int nackCountBefore, long id)
        => OutboxNackDeferralSchedule.FromOptions(options, new ZeroJitter()).CreatePlan()
            .GetDeferralForRow(nackCountBefore, id);

    private static OutboundMessage CreateMessage(string? orderingKey = null, string routingKey = "test.routing.key")
        => new(
            routingKey: routingKey,
            headers: orderingKey is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["x-ordering-key"] = orderingKey },
            body: "test-body"u8.ToArray(),
            contentType: "application/json");

    // Saves count messages (optionally keyed), claims them all and returns the claimed entries.
    private static async Task<IReadOnlyList<OutboxEntry>> SaveAndClaimAsync(
        InMemoryOutboxStore store, int count, string? key = null)
    {
        var messages = new List<OutboundMessage>(count);
        for (int i = 0; i < count; i++)
        {
            messages.Add(CreateMessage(key));
        }

        await store.SaveMessagesAsync(messages);
        return await store.GetPendingAsync(count);
    }

    // -------------------------------------------------------------------------
    // ReleaseLockAsync — deferral schedule and nack counter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReleaseLockAsync_Nack_SetsNotBeforeFromScheduleAndIncrementsNackCount()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());
        OutboxEntry entry = (await SaveAndClaimAsync(store, 1))[0];
        long id = entry.Id;

        IReadOnlySet<long> retained = await store.ReleaseLockAsync([id], []);

        retained.Should().BeEquivalentTo([id]);
        entry.NackCount.Should().Be(1);
        entry.NotBefore.Should().Be(T0 + ExpectedDeferral(Options, 0, id));
    }

    [Fact]
    public async Task ReleaseLockAsync_SecondNack_EscalatesToNextRetryBucket()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());
        OutboxEntry entry = (await SaveAndClaimAsync(store, 1))[0];
        long id = entry.Id;

        await store.ReleaseLockAsync([id], []);
        clock.Advance(ExpectedDeferral(Options, 0, id) + TimeSpan.FromTicks(1));
        (await store.GetPendingAsync(1)).Should().ContainSingle().Which.Id.Should().Be(id);

        DateTimeOffset t1 = clock.GetUtcNow();
        await store.ReleaseLockAsync([id], []);

        entry.NackCount.Should().Be(2);
        entry.NotBefore.Should().Be(t1 + ExpectedDeferral(Options, 1, id));
    }

    [Fact]
    public async Task ReleaseLockAsync_BarrierReleased_KeepsNackCountAndClearsNotBefore()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());
        OutboxEntry entry = (await SaveAndClaimAsync(store, 1))[0];
        long id = entry.Id;

        await store.ReleaseLockAsync([id], []);
        clock.Advance(ExpectedDeferral(Options, 0, id) + TimeSpan.FromTicks(1));
        await store.GetPendingAsync(1);

        await store.ReleaseLockAsync([], [id]);

        entry.NotBefore.Should().BeNull();
        entry.NackCount.Should().Be(1);
    }

    [Fact]
    public async Task ReleaseLockAsync_IdInBothLists_TreatedAsNackOnce()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());
        OutboxEntry entry = (await SaveAndClaimAsync(store, 1))[0];
        long id = entry.Id;

        IReadOnlySet<long> retained = await store.ReleaseLockAsync([id], [id]);

        retained.Should().BeEquivalentTo([id]);
        entry.NackCount.Should().Be(1, "the id in both lists must be treated as a single nack, not a nack plus a barrier release");
        entry.NotBefore.Should().Be(T0 + ExpectedDeferral(Options, 0, id));
    }

    // -------------------------------------------------------------------------
    // GetPendingAsync — bounded sweep, due-check, ordering, class split
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPendingAsync_AllEntriesDeferred_ReturnsEmptyBatchWithoutSpinning()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());
        long[] ids = [.. (await SaveAndClaimAsync(store, 1_000)).Select(e => e.Id)];
        await store.ReleaseLockAsync(ids, []);

        // A spinning sweep never returns — bound the call so the test fails instead of hanging.
        IReadOnlyList<OutboxEntry> batch = await Task.Run(() => store.GetPendingAsync(100).AsTask())
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        batch.Should().BeEmpty();
        clock.Advance(TimeSpan.FromSeconds(2));   // > max first-bucket deferral (1 s + 15%)
        (await store.GetPendingAsync(1_000)).Should().HaveCount(1_000);   // nothing lost by the sweep
    }

    [Fact]
    public async Task GetPendingAsync_NackedEntry_ClaimableOnlyAfterDeferralStrictlyElapsed()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());
        OutboxEntry entry = (await SaveAndClaimAsync(store, 1))[0];
        long id = entry.Id;
        await store.ReleaseLockAsync([id], []);

        TimeSpan deferral = ExpectedDeferral(Options, 0, id);
        clock.Advance(deferral);
        (await store.GetPendingAsync(1)).Should().BeEmpty("NotBefore has not yet strictly elapsed");

        clock.Advance(TimeSpan.FromTicks(1));
        (await store.GetPendingAsync(1)).Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task GetPendingAsync_DueRetries_OrderedByNotBeforeThenId()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());
        IReadOnlyList<OutboxEntry> claimed = await SaveAndClaimAsync(store, 3);
        long id1 = claimed[0].Id;
        long id2 = claimed[1].Id;
        long id3 = claimed[2].Id;

        // Nack id 3 first, then advance exactly the gap between the id-3 and id-1 base deferrals
        // (both nacked at NackCount 0) so nacking id 1 next lands on the very same instant.
        await store.ReleaseLockAsync([id3], []);
        TimeSpan gap = ExpectedDeferral(Options, 0, id3) - ExpectedDeferral(Options, 0, id1);
        clock.Advance(gap);
        await store.ReleaseLockAsync([id1], []);

        claimed[0].NotBefore.Should().Be(claimed[2].NotBefore, "id 1 and id 3 land on the same schedule cell and must tie exactly");

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await store.ReleaseLockAsync([id2], []);

        (claimed[1].NotBefore > claimed[0].NotBefore).Should().BeTrue("id 2 was nacked strictly later than the tied pair");
        (claimed[1].NotBefore > claimed[2].NotBefore).Should().BeTrue("id 2 was nacked strictly later than the tied pair");

        clock.Advance(TimeSpan.FromSeconds(3));
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        // Tie between id 1 and id 3 broken by Id; id 2 sorts last by NotBefore.
        batch.Select(e => e.Id).Should().Equal(id1, id3, id2);
    }

    [Theory]
    [InlineData(4, 4, 4, 3, 1)]
    [InlineData(4, 1, 4, 1, 3)]
    [InlineData(4, 4, 0, 4, 0)]
    [InlineData(2, 2, 2, 1, 1)]
    [InlineData(3, 3, 3, 2, 1)]
    [InlineData(8, 8, 8, 6, 2)]
    [InlineData(100, 100, 100, 75, 25)]
    public async Task GetPendingAsync_NewAndDueRetries_SplitsBatchByRetryReserve(
        int batchSize, int newCount, int retryCount, int expectedNew, int expectedRetries)
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());

        long[] retryIds = [];
        if (retryCount > 0)
        {
            IReadOnlyList<OutboxEntry> retryEntries = await SaveAndClaimAsync(store, retryCount);
            retryIds = [.. retryEntries.Select(e => e.Id)];
            await store.ReleaseLockAsync(retryIds, []);
            clock.Advance(TimeSpan.FromSeconds(3));
        }

        long[] expectedNewIds = [];
        if (newCount > 0)
        {
            await store.SaveMessagesAsync([.. Enumerable.Range(0, newCount).Select(_ => CreateMessage())]);
            expectedNewIds = [.. Enumerable.Range(retryCount + 1, newCount).Select(i => (long)i)];
        }

        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(batchSize);

        long[] batchNewIds = [.. batch.Select(e => e.Id).Where(id => expectedNewIds.Contains(id)).Order()];
        long[] batchRetryIds = [.. batch.Select(e => e.Id).Where(id => retryIds.Contains(id))];

        batch.Should().HaveCount(expectedNew + expectedRetries);
        batchNewIds.Should().Equal(expectedNewIds.Take(expectedNew), "the lowest-id new candidates fill the new slots");
        batchRetryIds.Should().HaveCount(expectedRetries);

        // Nothing lost: a large follow-up call returns exactly what was left behind.
        IReadOnlyList<OutboxEntry> remainder = await store.GetPendingAsync(1_000);
        remainder.Should().HaveCount(newCount + retryCount - expectedNew - expectedRetries);
    }

    [Fact]
    public async Task GetPendingAsync_BatchSizeOne_AlternatesClassesWhenBothWaiting()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());

        IReadOnlyList<OutboxEntry> retryEntries = await SaveAndClaimAsync(store, 2);
        long[] retryIds = [.. retryEntries.Select(e => e.Id)];
        await store.ReleaseLockAsync(retryIds, []);
        clock.Advance(TimeSpan.FromSeconds(3));

        await store.SaveMessagesAsync([CreateMessage(), CreateMessage()]);

        OutboxEntry first = (await store.GetPendingAsync(1)).Should().ContainSingle().Which;
        OutboxEntry second = (await store.GetPendingAsync(1)).Should().ContainSingle().Which;
        OutboxEntry third = (await store.GetPendingAsync(1)).Should().ContainSingle().Which;
        OutboxEntry fourth = (await store.GetPendingAsync(1)).Should().ContainSingle().Which;

        first.NotBefore.Should().BeNull("the first contested single-slot cycle serves the new class");
        second.NotBefore.Should().NotBeNull("the second cycle alternates to the retry class");
        third.NotBefore.Should().BeNull("the third cycle alternates back to the new class");
        fourth.NotBefore.Should().NotBeNull("only the retry class is left waiting for the fourth call");

        retryIds.Should().Contain(second.Id);
        retryIds.Should().Contain(fourth.Id);
    }

    [Fact]
    public async Task GetPendingAsync_PerKey_DeferredHeadBlocksItsKey()
    {
        var clock = new FakeTimeProvider(T0);
        OutboxOptions perKeyOptions = Options with { OrderingMode = OrderingMode.PerKey, OrderingKeyHeaderName = "x-ordering-key" };
        await using var store = new InMemoryOutboxStore(perKeyOptions, timeProvider: clock, jitterSource: new ZeroJitter());

        await store.SaveMessagesAsync([
            CreateMessage("A"),   // head of key A
            CreateMessage("A"),   // sibling, blocked until the head is delivered
            CreateMessage()       // keyless — always eligible
        ]);

        IReadOnlyList<OutboxEntry> firstBatch = await store.GetPendingAsync(10);
        firstBatch.Should().HaveCount(2, "head of key A and the keyless entry are both eligible");
        long headId = firstBatch.Single(e => e.OrderingKey == "A").Id;

        await store.ReleaseLockAsync([headId], []);

        IReadOnlyList<OutboxEntry> whileDeferred = await store.GetPendingAsync(10);
        whileDeferred.Should().BeEmpty("the deferred head still blocks key A and nothing else is pending");

        clock.Advance(ExpectedDeferral(perKeyOptions, 0, headId) + TimeSpan.FromTicks(1));

        IReadOnlyList<OutboxEntry> afterDeferral = await store.GetPendingAsync(10);
        afterDeferral.Should().ContainSingle("only the now-due head is claimable; its sibling stays blocked")
            .Which.Id.Should().Be(headId);
    }

    [Fact]
    public async Task GetPendingAsync_PerKey_AppliesRetryReserveAcrossKeyedAndKeylessCandidates()
    {
        var clock = new FakeTimeProvider(T0);
        OutboxOptions perKeyOptions = Options with { OrderingMode = OrderingMode.PerKey, OrderingKeyHeaderName = "x-ordering-key" };
        await using var store = new InMemoryOutboxStore(perKeyOptions, timeProvider: clock, jitterSource: new ZeroJitter());

        await store.SaveMessagesAsync([
            CreateMessage("A"),   // head of key A — stays in the "new" class
            CreateMessage("B")    // head of key B — nacked into the "retry" class below
        ]);

        IReadOnlyList<OutboxEntry> heads = await store.GetPendingAsync(10);
        long headA = heads.Single(e => e.OrderingKey == "A").Id;
        long headB = heads.Single(e => e.OrderingKey == "B").Id;

        // headA goes back through the barrier list (stays "new"); headB is nacked ("retry").
        await store.ReleaseLockAsync(nackedIds: [headB], barrierReleasedIds: [headA]);
        clock.Advance(ExpectedDeferral(perKeyOptions, 0, headB) + TimeSpan.FromSeconds(3));

        // Two more keyless "new" candidates with higher ids than headA — they must lose the
        // single new slot to the lowest-id candidate.
        await store.SaveMessagesAsync([CreateMessage(), CreateMessage()]);

        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(2);

        batch.Select(e => e.Id).Should().BeEquivalentTo([headA, headB],
            "the single new slot goes to the lowest-id new candidate (head of key A) and the single retry slot to the only due retry (head of key B)");
    }

    [Fact]
    public async Task GetPendingAsync_ClaimedEntryNeverReleased_IsNotHandedOutAgain()
    {
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(Options, timeProvider: clock, jitterSource: new ZeroJitter());
        await SaveAndClaimAsync(store, 1);

        clock.Advance(Options.OutboxLockTimeout + Options.OutboxLockTimeout);

        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        batch.Should().BeEmpty("an entry claimed and never released or delivered is never handed out again");
    }
}
