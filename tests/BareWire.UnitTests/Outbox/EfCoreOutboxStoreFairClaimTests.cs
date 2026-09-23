using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Outbox;

/// <summary>
/// Drives <see cref="EfCoreOutboxStore.GetPendingAsync"/> against SQLite (client-side claim
/// fallback, since SQLite never matches <see cref="PostgresOutboxSqlDialect.ProviderName"/>) to
/// exercise the fair two-class claim split — reservation, due-time ordering, single-slot turn
/// alternation, top-up, carry-forward, and per-key head-of-line ordering.
/// </summary>
public sealed class EfCoreOutboxStoreFairClaimTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly OutboxOptions _options = new();
    private SqliteConnection _connection = null!;
    private OutboxDbContext _dbContext = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _dbContext = new OutboxDbContext(
            new DbContextOptionsBuilder<OutboxDbContext>().UseSqlite(_connection).Options);
        await _dbContext.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ── Reservation splits an abundant supply of both classes ────────────────

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(3, 2, 1)]
    [InlineData(4, 3, 1)]
    [InlineData(100, 75, 25)]
    public async Task GetPendingAsync_DueRetriesFillWholeBatch_ReservesSlotsForNewRows(
        int n,
        int expectedNew,
        int expectedRetries)
    {
        OutboxMessage[] dueRows = Enumerable.Range(0, 2 * n)
            .Select(i => DueRow(i + 1))
            .ToArray();
        await SeedAsync(dueRows);

        OutboxMessage[] newRows = Enumerable.Range(0, 2 * n).Select(_ => NewRow()).ToArray();
        await SeedAsync(newRows);

        EfCoreOutboxStore store = Store("a", new FakeTimeProvider(T0), new FixedJitterSource(0.5));
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(n);
        try
        {
            batch.Should().HaveCount(n);

            HashSet<long> dueIds = dueRows.Select(r => r.Id).ToHashSet();
            int retryCount = batch.Count(e => dueIds.Contains(e.Id));
            int newCount = batch.Count - retryCount;

            newCount.Should().Be(expectedNew);
            retryCount.Should().Be(expectedRetries);
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    // ── No due retries: the whole batch tops up with new rows ────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(100)]
    public async Task GetPendingAsync_NoDueRetries_TopsUpWholeBatchWithNewRows(int n)
    {
        OutboxMessage[] newRows = Enumerable.Range(0, 2 * n).Select(_ => NewRow()).ToArray();
        await SeedAsync(newRows);

        EfCoreOutboxStore store = Store("a", new FakeTimeProvider(T0), new FixedJitterSource(0.5));
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(n);
        try
        {
            long[] expected = [.. newRows.Take(n).Select(r => r.Id)];
            batch.Select(e => e.Id).Should().Equal(expected,
                "the top-up step must fill the whole batch with the earliest new rows in id order");
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    // ── Few new rows: the remainder goes to due retries ───────────────────────

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(100, 10)]
    public async Task GetPendingAsync_FewNewRows_RemainderGoesToDueRetries(int n, int newRowCount)
    {
        OutboxMessage[] dueRows = Enumerable.Range(0, 2 * n)
            .Select(i => DueRow(i + 1))
            .ToArray();
        await SeedAsync(dueRows);

        OutboxMessage[] newRows = Enumerable.Range(0, newRowCount).Select(_ => NewRow()).ToArray();
        await SeedAsync(newRows);

        EfCoreOutboxStore store = Store("a", new FakeTimeProvider(T0), new FixedJitterSource(0.5));
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(n);
        try
        {
            batch.Should().HaveCount(n);
            HashSet<long> claimedIds = batch.Select(e => e.Id).ToHashSet();
            claimedIds.Should().Contain(newRows.Select(r => r.Id),
                "every new row must be claimed when the new-row supply is short of its reservation");
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    // ── Due retries are claimed by due time, not by id ────────────────────────

    [Fact]
    public async Task GetPendingAsync_DueRetries_ClaimedInDueTimeOrderNotIdOrder()
    {
        // Lower id (added first) is due LATER; higher id (added second) is due EARLIER — the store
        // must prefer due time over id when only retries are waiting.
        OutboxMessage laterDue = DueRow(1);
        OutboxMessage earlierDue = DueRow(30);
        await SeedAsync(laterDue, earlierDue);

        EfCoreOutboxStore storeA = Store("a", new FakeTimeProvider(T0), new FixedJitterSource(0.5));
        IReadOnlyList<OutboxEntry> first = await storeA.GetPendingAsync(1);
        try
        {
            first.Should().ContainSingle().Which.Id.Should().Be(earlierDue.Id,
                "the longest-overdue row must be claimed first, regardless of id order");
        }
        finally
        {
            ReturnBuffers(first);
        }

        // Three retries at distinct due times, claimed two at a time.
        OutboxMessage r1 = DueRow(5);
        OutboxMessage r2 = DueRow(50);
        OutboxMessage r3 = DueRow(25);
        await SeedAsync(r1, r2, r3);

        EfCoreOutboxStore storeB = Store("b", new FakeTimeProvider(T0), new FixedJitterSource(0.5));
        IReadOnlyList<OutboxEntry> second = await storeB.GetPendingAsync(2);
        try
        {
            second.Select(e => e.Id).Should().Equal([r2.Id, r3.Id],
                "the two most overdue rows must be claimed before the least overdue one");
        }
        finally
        {
            ReturnBuffers(second);
        }
    }

    // ── Single-slot batch: the contested turn alternates by turn ────────────

    [Fact]
    public async Task GetPendingAsync_SingleSlotBothClassesWaiting_AlternatesByTurn()
    {
        OutboxMessage retryRow = DueRow(5);
        OutboxMessage newRowFirst = NewRow();
        await SeedAsync(retryRow, newRowFirst);

        var clock = new FakeTimeProvider(T0);
        var turn = new OutboxSingleSlotTurn();
        EfCoreOutboxStore store = Store("a", clock, new FixedJitterSource(0.5), turn: turn);

        // First consultation of the shared turn -> new class.
        IReadOnlyList<OutboxEntry> first = await store.GetPendingAsync(1);
        try
        {
            first.Should().ContainSingle().Which.Id.Should().Be(newRowFirst.Id);
        }
        finally
        {
            ReturnBuffers(first);
        }

        await store.MarkDeliveredAsync([newRowFirst.Id]);
        OutboxMessage newRowSecond = NewRow();
        await SeedAsync(newRowSecond);

        // Second consultation of the shared turn -> retry class, even though a new row waits.
        IReadOnlyList<OutboxEntry> second = await store.GetPendingAsync(1);
        try
        {
            second.Should().ContainSingle().Which.Id.Should().Be(retryRow.Id);
        }
        finally
        {
            ReturnBuffers(second);
        }
    }

    // ── Single-slot batch: only one class waiting is always served ───────────

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task GetPendingAsync_SingleSlotOneClassOnly_ServesItRegardlessOfTurn(
        bool retryTurnFirst,
        bool onlyNew)
    {
        OutboxMessage row = onlyNew ? NewRow() : DueRow(5);
        await SeedAsync(row);

        var turn = new OutboxSingleSlotTurn();
        if (retryTurnFirst)
        {
            // Consult the turn once before the claim, so its state favors the retry class next.
            turn.NextIsRetryTurn();
        }

        EfCoreOutboxStore store = Store("a", new FakeTimeProvider(T0), new FixedJitterSource(0.5), turn: turn);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(1);
        try
        {
            batch.Should().ContainSingle().Which.Id.Should().Be(row.Id,
                "the only waiting class must be served no matter which class won the contested turn");
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    // ── Poison cohort: a large always-nacked retry backlog cannot starve new rows ──

    [Fact]
    public async Task GetPendingAsync_PoisonCohortAlwaysNacked_AllNewRowsDeliveredWithinBoundedCycles()
    {
        // Poison cohort: seeded directly as due retries (already overdue), simulating rows that
        // have already failed at least once and are ready to be reclaimed on the very first cycle.
        OutboxMessage[] poisonRows = Enumerable.Range(0, 8)
            .Select(i => DueRow(2 * (i + 1), retryCount: 1))
            .ToArray();
        await SeedAsync(poisonRows);
        HashSet<long> poisonIds = poisonRows.Select(r => r.Id).ToHashSet();

        OutboxMessage[] newRows = Enumerable.Range(0, 9).Select(_ => NewRow()).ToArray();
        await SeedAsync(newRows);
        HashSet<long> newIds = newRows.Select(r => r.Id).ToHashSet();

        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore store = Store("a", clock, new FixedJitterSource(0.5));
        var delivered = new HashSet<long>();

        for (int cycle = 0; cycle < 3; cycle++)
        {
            IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(4);
            try
            {
                List<long> poisonInBatch = [.. batch.Select(e => e.Id).Where(poisonIds.Contains)];
                List<long> othersInBatch = [.. batch.Select(e => e.Id).Where(id => !poisonIds.Contains(id))];

                if (poisonInBatch.Count > 0)
                {
                    await store.ReleaseLockAsync(poisonInBatch, []);
                }

                if (othersInBatch.Count > 0)
                {
                    await store.MarkDeliveredAsync(othersInBatch);
                    delivered.UnionWith(othersInBatch);
                }
            }
            finally
            {
                ReturnBuffers(batch);
            }

            clock.Advance(_options.OutboxLockTimeout * 1.25);
        }

        delivered.Should().BeEquivalentTo(newIds, "every new row must be delivered within the bounded cycle count");
    }

    // ── A single transient retry behind a poison cohort is still reclaimed ───

    [Fact]
    public async Task GetPendingAsync_TransientRetryBehindPoisonCohort_ReclaimedWithinBoundedCycles()
    {
        OutboxMessage[] poisonRows = Enumerable.Range(0, 8)
            .Select(i => DueRow(2 * (i + 1), retryCount: 1))
            .ToArray();
        await SeedAsync(poisonRows);
        HashSet<long> poisonIds = poisonRows.Select(r => r.Id).ToHashSet();

        // A row nacked exactly once, with a due time between the poison rows' due times, so it
        // competes in due-time order rather than being trivially first or last.
        OutboxMessage transient = DueRow(9, retryCount: 1);
        await SeedAsync(transient);

        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore store = Store("a", clock, new FixedJitterSource(0.5));
        bool transientDelivered = false;

        const int maxCycles = 9; // ceil((8 poison + 1 transient) / 1 reserved retry slot per cycle)
        for (int cycle = 0; cycle < maxCycles && !transientDelivered; cycle++)
        {
            IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(4);
            try
            {
                if (batch.Any(e => e.Id == transient.Id))
                {
                    await store.MarkDeliveredAsync([transient.Id]);
                    transientDelivered = true;
                }

                List<long> poisonInBatch = [.. batch.Select(e => e.Id).Where(poisonIds.Contains)];
                if (poisonInBatch.Count > 0)
                {
                    await store.ReleaseLockAsync(poisonInBatch, []);
                }
            }
            finally
            {
                ReturnBuffers(batch);
            }

            clock.Advance(_options.OutboxLockTimeout * 1.25);
        }

        transientDelivered.Should().BeTrue(
            $"the transient retry must eventually be reclaimed and delivered within {maxCycles} cycles");
    }

    // ── An abandoned claim behind a poison cohort is reclaimed once it goes stale ──

    [Fact]
    public async Task GetPendingAsync_AbandonedClaimBehindPoisonCohort_ReclaimedWithinBoundedCycles()
    {
        OutboxMessage[] poisonRows = Enumerable.Range(0, 8)
            .Select(i => DueRow(2 * (i + 1), retryCount: 1))
            .ToArray();
        await SeedAsync(poisonRows);
        HashSet<long> poisonIds = poisonRows.Select(r => r.Id).ToHashSet();

        // Locked by a dead instance at T0 — not stale yet at the start of the test.
        OutboxMessage abandoned = NewRow();
        abandoned.LockedAt = T0;
        abandoned.LockedBy = "dead";
        await SeedAsync(abandoned);

        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore store = Store("a", clock, new FixedJitterSource(0.5));

        // Advance past OutboxLockTimeout first so the abandoned claim becomes stale.
        clock.Advance(_options.OutboxLockTimeout + TimeSpan.FromSeconds(1));

        bool abandonedDelivered = false;
        const int maxCycles = 9;
        for (int cycle = 0; cycle < maxCycles && !abandonedDelivered; cycle++)
        {
            IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(4);
            try
            {
                if (batch.Any(e => e.Id == abandoned.Id))
                {
                    await store.MarkDeliveredAsync([abandoned.Id]);
                    abandonedDelivered = true;
                }

                List<long> poisonInBatch = [.. batch.Select(e => e.Id).Where(poisonIds.Contains)];
                if (poisonInBatch.Count > 0)
                {
                    await store.ReleaseLockAsync(poisonInBatch, []);
                }
            }
            finally
            {
                ReturnBuffers(batch);
            }

            clock.Advance(_options.OutboxLockTimeout * 1.25);
        }

        abandonedDelivered.Should().BeTrue(
            $"the abandoned claim must eventually be reclaimed and delivered within {maxCycles} cycles " +
            "once its lock goes stale");
    }

    // ── Per-key head-of-line: a deferred head blocks a newer row of the same key ──

    [Fact]
    public async Task GetPendingAsync_PerKeyDueHeadDeferred_BlocksNewerRowsOfSameKey()
    {
        OutboxOptions perKeyOptions = _options with
        {
            OrderingMode = OrderingMode.PerKey,
            OrderingKeyHeaderName = "x-ordering-key",
        };

        OutboxMessage row1 = DueRow(1, orderingKey: "k");
        OutboxMessage row2 = NewRow();
        row2.OrderingKey = "k";
        OutboxMessage row3 = NewRow();
        await SeedAsync(row1, row2, row3);

        EfCoreOutboxStore store = Store("a", new FakeTimeProvider(T0), new FixedJitterSource(0.5), perKeyOptions);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(4);
        try
        {
            batch.Select(e => e.Id).Should().BeEquivalentTo([row1.Id, row3.Id],
                "row2 shares a key with the still-undelivered head (row1) and must stay blocked");
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    // ── Carry-forward never claims beyond batchSize ───────────────────────────

    [Fact]
    public async Task GetPendingAsync_CarryForwardAfterFailedSend_DoesNotClaimBeyondBatchSize()
    {
        OutboxMessage[] rows = Enumerable.Range(0, 10).Select(_ => NewRow()).ToArray();
        await SeedAsync(rows);

        EfCoreOutboxStore store = Store("a", new FakeTimeProvider(T0), new FixedJitterSource(0.5));

        IReadOnlyList<OutboxEntry> first = await store.GetPendingAsync(4);
        IReadOnlyList<OutboxEntry> second = Array.Empty<OutboxEntry>();
        try
        {
            first.Should().HaveCount(4);
            HashSet<long> firstIds = first.Select(e => e.Id).ToHashSet();

            second = await store.GetPendingAsync(4);
            second.Select(e => e.Id).ToHashSet().Should().BeEquivalentTo(firstIds,
                "carry-forward must reclaim the same owned rows this instance already holds, not new ones");

            int ownedCount = await _dbContext.Set<OutboxMessage>()
                .AsNoTracking()
                .CountAsync(m => m.LockedBy == "a" && m.DeliveredAt == null);
            ownedCount.Should().Be(4, "this instance must not own more than batchSize rows after two claim cycles");
        }
        finally
        {
            ReturnBuffers(first);
            ReturnBuffers(second);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static OutboxMessage NewRow()
        => new()
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "test.routing",
            ContentType = "application/json",
            Payload = [1],
            CreatedAt = T0,
        };

    private static OutboxMessage DueRow(int overdueBySeconds, int retryCount = 1, string? orderingKey = null)
        => new()
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "test.routing",
            ContentType = "application/json",
            Payload = [1],
            CreatedAt = T0,
            LockedAt = T0 - new OutboxOptions().OutboxLockTimeout - TimeSpan.FromSeconds(overdueBySeconds),
            LockedBy = null,
            RetryCount = retryCount,
            OrderingKey = orderingKey,
        };

    private async Task<List<long>> SeedAsync(params OutboxMessage[] rows)
    {
        _dbContext.OutboxMessages.AddRange(rows);
        await _dbContext.SaveChangesAsync();
        return [.. rows.Select(r => r.Id)];
    }

    private EfCoreOutboxStore Store(
        string instance,
        TimeProvider clock,
        IOutboxJitterSource jitter,
        OutboxOptions? options = null,
        OutboxSingleSlotTurn? turn = null)
        => new(
            _dbContext,
            new OutboxInstanceId(instance),
            new PostgresOutboxSqlDialect(),
            options ?? _options,
            clock,
            jitter,
            turn);

    private static void ReturnBuffers(IReadOnlyList<OutboxEntry> batch)
    {
        foreach (OutboxEntry entry in batch)
        {
            ArrayPool<byte>.Shared.Return(entry.PooledBody);
        }
    }

    private sealed class FixedJitterSource(double value) : IOutboxJitterSource
    {
        public double NextDouble() => value;
    }
}
