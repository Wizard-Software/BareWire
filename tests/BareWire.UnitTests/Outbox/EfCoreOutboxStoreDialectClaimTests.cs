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
/// Drives <see cref="EfCoreOutboxStore.GetPendingAsync"/> through the dialect claim path — SQLite
/// registered under a custom <see cref="IOutboxSqlDialect"/> whose <see cref="IOutboxSqlDialect.ProviderName"/>
/// matches the active provider, so the store issues real claim statements instead of the client-side
/// fallback. Two test dialects are used: one implementing only the public claim statement, and one
/// also implementing <see cref="INewRowsClaimSql"/> / <see cref="IDueOrderedRetryClaimSql"/> — both
/// record every claim statement they are asked to render.
/// </summary>
public sealed class EfCoreOutboxStoreDialectClaimTests : IAsyncLifetime
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

    [Fact]
    public void Database_ProviderName_IsSqliteProviderNameUsedByTestDialects()
    {
        _dbContext.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.Sqlite");
    }

    // ── Reservation splits an abundant supply of both classes ────────────────

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(3, 2, 1)]
    [InlineData(4, 3, 1)]
    [InlineData(100, 75, 25)]
    public async Task GetPendingAsync_DueOrderedDialect_SplitsBatchBetweenClasses(
        int n,
        int expectedNew,
        int expectedRetries)
    {
        var dialect = new SqliteDueOrderedDialect();

        await SeedDueRowsAsync(2 * n);
        await SeedNewRowsAsync(2 * n);

        EfCoreOutboxStore store = Store(dialect, "a");
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(n);
        try
        {
            batch.Should().HaveCount(n);
            dialect.Calls.Should().HaveCount(2);
            dialect.Calls[0].Kind.Should().Be("new");
            dialect.Calls[0].Limit.Should().Be(expectedNew);
            dialect.Calls[1].Kind.Should().Be("due");
            dialect.Calls[1].Limit.Should().Be(n - expectedNew);
            dialect.Calls[1].Limit.Should().Be(expectedRetries);
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    [Fact]
    public async Task GetPendingAsync_DueOrderedDialect_RetriesInDueTimeOrder()
    {
        var dialect = new SqliteDueOrderedDialect();

        OutboxMessage r1 = DueRow(5);
        OutboxMessage r2 = DueRow(50);
        OutboxMessage r3 = DueRow(25);
        await SeedAsync(r1, r2, r3);

        EfCoreOutboxStore store = Store(dialect, "a");
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(2);
        try
        {
            batch.Select(e => e.Id).Should().Equal([r2.Id, r3.Id],
                "the two most overdue rows must be claimed first, in due-time order");
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(100)]
    public async Task GetPendingAsync_DueOrderedDialectFewRetries_TopsUpWithNewRows(int n)
    {
        var dialect = new SqliteDueOrderedDialect();
        await SeedNewRowsAsync(2 * n);

        EfCoreOutboxStore store = Store(dialect, "a");
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(n);
        try
        {
            batch.Should().HaveCount(n);
            dialect.Calls.Should().HaveCount(3);
            dialect.Calls[0].Kind.Should().Be("new");
            dialect.Calls[1].Kind.Should().Be("due");
            dialect.Calls[2].Kind.Should().Be("new");
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    [Fact]
    public async Task GetPendingAsync_DueOrderedDialectStepOneShort_SkipsTopUp()
    {
        var dialect = new SqliteDueOrderedDialect();
        await SeedNewRowsAsync(1);

        EfCoreOutboxStore store = Store(dialect, "a");
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(4);
        try
        {
            batch.Should().HaveCount(1);
            dialect.Calls.Should().HaveCount(2,
                "the new-row supply was exhausted before its reservation, so no top-up step should run");
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    [Theory]
    [InlineData(0, "new")]
    [InlineData(1, "due")]
    public async Task GetPendingAsync_DueOrderedDialectSingleSlot_ServesClassOfTurn(
        int priorTurns,
        string expectedFirstCallKind)
    {
        var dialect = new SqliteDueOrderedDialect();

        OutboxMessage newRow = NewRow();
        OutboxMessage dueRow = DueRow(5);
        await SeedAsync(newRow, dueRow);

        var turn = new OutboxSingleSlotTurn();
        for (int i = 0; i < priorTurns; i++)
        {
            turn.NextIsRetryTurn();
        }

        EfCoreOutboxStore store = Store(dialect, "a", turn: turn);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(1);
        try
        {
            batch.Should().ContainSingle();
            dialect.Calls.Should().NotBeEmpty();
            dialect.Calls[0].Kind.Should().Be(expectedFirstCallKind);
            dialect.Calls[0].Limit.Should().Be(1);
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    // ── Capability fallback: a dialect without the new/due overloads always uses the public SQL ──

    [Fact]
    public async Task GetPendingAsync_CustomDialectWithoutCapability_UsesPublicClaimSqlForEveryStep()
    {
        var dialect = new SqliteClaimDialect();

        await SeedNewRowsAsync(8);
        await SeedDueRowsAsync(8);

        EfCoreOutboxStore store = Store(dialect, "a");
        DateTimeOffset staleCutoff = T0 - _options.OutboxLockTimeout;

        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(4);
        try
        {
            batch.Should().HaveCount(4);
            dialect.Calls.Should().HaveCount(2);
            dialect.Calls[0].Kind.Should().Be("public");
            dialect.Calls[0].Limit.Should().Be(3);
            dialect.Calls[0].Cutoff.Should().Be(OutboxFairClaimPlan.NewRowsOnlyCutoff);
            dialect.Calls[1].Kind.Should().Be("public");
            dialect.Calls[1].Limit.Should().Be(1);
            dialect.Calls[1].Cutoff.Should().Be(staleCutoff);
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    // ── Carry-forward across the dialect path ─────────────────────────────────

    [Fact]
    public async Task GetPendingAsync_DialectPathCarryForward_ClaimsOnlyRemainingCapacity()
    {
        var dialect = new SqliteDueOrderedDialect();
        await SeedNewRowsAsync(10);

        EfCoreOutboxStore store = Store(dialect, "a");
        IReadOnlyList<OutboxEntry> first = await store.GetPendingAsync(4);
        first.Should().HaveCount(4);
        HashSet<long> firstIds = first.Select(e => e.Id).ToHashSet();

        dialect.Calls.Clear();
        await SeedNewRowsAsync(5);

        IReadOnlyList<OutboxEntry> second = Array.Empty<OutboxEntry>();
        try
        {
            second = await store.GetPendingAsync(4);
            second.Select(e => e.Id).ToHashSet().Should().BeEquivalentTo(firstIds);
            dialect.Calls.Should().BeEmpty("carry-forward leaves no capacity for a new claim step");
        }
        finally
        {
            ReturnBuffers(first);
            ReturnBuffers(second);
        }
    }

    [Fact]
    public async Task GetPendingAsync_DialectPathStaleOwnCarryForward_ReclaimedAndRestamped()
    {
        var dialect = new SqliteDueOrderedDialect();
        await SeedNewRowsAsync(4);

        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore store = Store(dialect, "a", clock);

        IReadOnlyList<OutboxEntry> first = await store.GetPendingAsync(4);
        first.Should().HaveCount(4);
        HashSet<long> ids = first.Select(e => e.Id).ToHashSet();
        ReturnBuffers(first);

        clock.Advance(_options.OutboxLockTimeout + TimeSpan.FromSeconds(1));
        DateTimeOffset newNow = clock.GetUtcNow();

        IReadOnlyList<OutboxEntry> second = await store.GetPendingAsync(4);
        try
        {
            second.Select(e => e.Id).ToHashSet().Should().BeEquivalentTo(ids);

            List<OutboxMessage> rows = await _dbContext.Set<OutboxMessage>()
                .AsNoTracking()
                .Where(m => ids.Contains(m.Id))
                .ToListAsync();
            rows.Should().AllSatisfy(r => r.LockedAt.Should().Be(newNow));
        }
        finally
        {
            ReturnBuffers(second);
        }
    }

    [Fact]
    public async Task GetPendingAsync_DialectPathStaleOwnCarryForwardClaimedByOther_NotReturnedToOwner()
    {
        await SeedNewRowsAsync(4);

        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store(new SqliteDueOrderedDialect(), "a", clock);

        IReadOnlyList<OutboxEntry> first = await storeA.GetPendingAsync(4);
        first.Should().HaveCount(4);
        HashSet<long> ids = first.Select(e => e.Id).ToHashSet();
        ReturnBuffers(first);

        clock.Advance(_options.OutboxLockTimeout + TimeSpan.FromSeconds(1));

        EfCoreOutboxStore storeB = Store(new SqliteDueOrderedDialect(), "b", clock);
        IReadOnlyList<OutboxEntry> claimedByB = await storeB.GetPendingAsync(4);
        try
        {
            claimedByB.Select(e => e.Id).ToHashSet().Should().BeEquivalentTo(ids,
                "instance b must be able to reclaim a's now-stale rows");
        }
        finally
        {
            ReturnBuffers(claimedByB);
        }

        IReadOnlyList<OutboxEntry> second = await storeA.GetPendingAsync(4);
        try
        {
            second.Should().BeEmpty("a's stale rows now belong to b and must not be returned to a");
        }
        finally
        {
            ReturnBuffers(second);
        }
    }

    [Fact]
    public async Task GetPendingAsync_DialectPathStaleOwnRowsNotReclaimedThisCycle_NotReturnedToOwner()
    {
        // Rows "a" still owns with an expired lock (lower ids) and two deferred rows that fell due earlier.
        // With a batch of two, the due-ordered step claims the earlier-due deferred rows, so a's own stale
        // rows are not re-stamped this cycle: they stay claimable by any instance and must not be sent by a.
        OutboxMessage staleOwnA = NewRow();
        staleOwnA.LockedBy = "a";
        staleOwnA.LockedAt = T0 - _options.OutboxLockTimeout - TimeSpan.FromSeconds(5);
        OutboxMessage staleOwnB = NewRow();
        staleOwnB.LockedBy = "a";
        staleOwnB.LockedAt = T0 - _options.OutboxLockTimeout - TimeSpan.FromSeconds(5);
        await SeedAsync(staleOwnA, staleOwnB);
        List<long> dueEarlier = await SeedAsync(DueRow(60), DueRow(61));

        IReadOnlyList<OutboxEntry> batch = await Store(new SqliteDueOrderedDialect(), "a").GetPendingAsync(2);
        try
        {
            batch.Select(e => e.Id).Should().BeEquivalentTo(dueEarlier,
                "own rows whose lock is still stale were not re-claimed and must not be handed back");
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    // ── Per-key head-of-line through the dialect path ─────────────────────────

    [Fact]
    public async Task GetPendingAsync_DueOrderedDialectPerKey_DeferredHeadBlocksNewerRowOfSameKey()
    {
        var dialect = new SqliteDueOrderedDialect();
        OutboxOptions perKeyOptions = _options with
        {
            OrderingMode = OrderingMode.PerKey,
            OrderingKeyHeaderName = "x-ordering-key",
        };

        OutboxMessage head = DueRow(1, orderingKey: "k");
        OutboxMessage sameKeyNewer = NewRow();
        sameKeyNewer.OrderingKey = "k";
        OutboxMessage keyless = NewRow();
        await SeedAsync(head, sameKeyNewer, keyless);

        EfCoreOutboxStore store = Store(dialect, "a", options: perKeyOptions);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(4);
        try
        {
            batch.Select(e => e.Id).Should().BeEquivalentTo([head.Id, keyless.Id],
                "the newer same-key row must stay blocked behind its still-undelivered head");
        }
        finally
        {
            ReturnBuffers(batch);
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

    private static OutboxMessage DueRow(int overdueBySeconds, string? orderingKey = null)
        => new()
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "test.routing",
            ContentType = "application/json",
            Payload = [1],
            CreatedAt = T0,
            LockedAt = T0 - new OutboxOptions().OutboxLockTimeout - TimeSpan.FromSeconds(overdueBySeconds),
            LockedBy = null,
            RetryCount = 1,
            OrderingKey = orderingKey,
        };

    private async Task<List<long>> SeedAsync(params OutboxMessage[] rows)
    {
        _dbContext.OutboxMessages.AddRange(rows);
        await _dbContext.SaveChangesAsync();
        return [.. rows.Select(r => r.Id)];
    }

    private Task<List<long>> SeedNewRowsAsync(int count)
        => SeedAsync([.. Enumerable.Range(0, count).Select(_ => NewRow())]);

    private Task<List<long>> SeedDueRowsAsync(int count)
        => SeedAsync([.. Enumerable.Range(0, count).Select(i => DueRow(i + 1))]);

    private EfCoreOutboxStore Store(
        IOutboxSqlDialect dialect,
        string instance,
        TimeProvider? clock = null,
        IOutboxJitterSource? jitter = null,
        OutboxOptions? options = null,
        OutboxSingleSlotTurn? turn = null)
        => new(
            _dbContext,
            new OutboxInstanceId(instance),
            dialect,
            options ?? _options,
            clock ?? new FakeTimeProvider(T0),
            jitter ?? new FixedJitterSource(0.5),
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

    /// <summary>
    /// Test dialect implementing only the public 4-/5-arg claim statement (no <see cref="INewRowsClaimSql"/>
    /// / <see cref="IDueOrderedRetryClaimSql"/>), so every claim step falls back to it.
    /// </summary>
    private sealed class SqliteClaimDialect : IOutboxSqlDialect
    {
        public List<(string Kind, int Limit, DateTimeOffset? Cutoff)> Calls { get; } = [];

        public string ProviderName => "Microsoft.EntityFrameworkCore.Sqlite";

        public bool SupportsPerKeyHeadOfLineOrdering => true;

        public FormattableString GetClaimSql(
            string instanceId,
            DateTimeOffset now,
            DateTimeOffset staleCutoff,
            int batchSize)
        {
            Calls.Add(("public", batchSize, staleCutoff));
            return $"""
                UPDATE "OutboxMessages"
                SET "LockedAt" = {now}, "LockedBy" = {instanceId}
                WHERE "Id" IN (
                  SELECT "Id" FROM "OutboxMessages"
                  WHERE "DeliveredAt" IS NULL AND ("LockedAt" IS NULL OR "LockedAt" < {staleCutoff})
                  ORDER BY "Id"
                  LIMIT {batchSize}
                )
                """;
        }

        public FormattableString GetClaimSql(
            string instanceId,
            DateTimeOffset now,
            DateTimeOffset staleCutoff,
            int batchSize,
            OrderingMode orderingMode)
        {
            if (orderingMode != OrderingMode.PerKey)
            {
                return GetClaimSql(instanceId, now, staleCutoff, batchSize);
            }

            Calls.Add(("public", batchSize, staleCutoff));
            return $"""
                UPDATE "OutboxMessages"
                SET "LockedAt" = {now}, "LockedBy" = {instanceId}
                WHERE "Id" IN (
                  SELECT o."Id" FROM "OutboxMessages" o
                  WHERE o."DeliveredAt" IS NULL AND (o."LockedAt" IS NULL OR o."LockedAt" < {staleCutoff})
                    AND (o."OrderingKey" IS NULL OR NOT EXISTS (
                      SELECT 1 FROM "OutboxMessages" e
                      WHERE e."OrderingKey" = o."OrderingKey" AND e."DeliveredAt" IS NULL AND e."Id" < o."Id"))
                  ORDER BY o."Id"
                  LIMIT {batchSize}
                )
                """;
        }
    }

    /// <summary>
    /// Test dialect implementing the public claim statement plus <see cref="INewRowsClaimSql"/> and
    /// <see cref="IDueOrderedRetryClaimSql"/> — the shape a fair claim cycle prefers.
    /// </summary>
    private sealed class SqliteDueOrderedDialect : IOutboxSqlDialect, INewRowsClaimSql, IDueOrderedRetryClaimSql
    {
        public List<(string Kind, int Limit, DateTimeOffset? Cutoff)> Calls { get; } = [];

        public string ProviderName => "Microsoft.EntityFrameworkCore.Sqlite";

        public bool SupportsPerKeyHeadOfLineOrdering => true;

        public FormattableString GetClaimSql(
            string instanceId,
            DateTimeOffset now,
            DateTimeOffset staleCutoff,
            int batchSize)
        {
            Calls.Add(("public", batchSize, staleCutoff));
            return $"""
                UPDATE "OutboxMessages"
                SET "LockedAt" = {now}, "LockedBy" = {instanceId}
                WHERE "Id" IN (
                  SELECT "Id" FROM "OutboxMessages"
                  WHERE "DeliveredAt" IS NULL AND ("LockedAt" IS NULL OR "LockedAt" < {staleCutoff})
                  ORDER BY "Id"
                  LIMIT {batchSize}
                )
                """;
        }

        public FormattableString GetClaimSql(
            string instanceId,
            DateTimeOffset now,
            DateTimeOffset staleCutoff,
            int batchSize,
            OrderingMode orderingMode)
        {
            if (orderingMode != OrderingMode.PerKey)
            {
                return GetClaimSql(instanceId, now, staleCutoff, batchSize);
            }

            Calls.Add(("public", batchSize, staleCutoff));
            return $"""
                UPDATE "OutboxMessages"
                SET "LockedAt" = {now}, "LockedBy" = {instanceId}
                WHERE "Id" IN (
                  SELECT o."Id" FROM "OutboxMessages" o
                  WHERE o."DeliveredAt" IS NULL AND (o."LockedAt" IS NULL OR o."LockedAt" < {staleCutoff})
                    AND (o."OrderingKey" IS NULL OR NOT EXISTS (
                      SELECT 1 FROM "OutboxMessages" e
                      WHERE e."OrderingKey" = o."OrderingKey" AND e."DeliveredAt" IS NULL AND e."Id" < o."Id"))
                  ORDER BY o."Id"
                  LIMIT {batchSize}
                )
                """;
        }

        public FormattableString GetNewRowsClaimSql(
            string instanceId,
            DateTimeOffset now,
            int batchSize,
            OrderingMode orderingMode)
        {
            Calls.Add(("new", batchSize, null));

            if (orderingMode != OrderingMode.PerKey)
            {
                return $"""
                    UPDATE "OutboxMessages"
                    SET "LockedAt" = {now}, "LockedBy" = {instanceId}
                    WHERE "Id" IN (
                      SELECT "Id" FROM "OutboxMessages"
                      WHERE "DeliveredAt" IS NULL AND "LockedAt" IS NULL
                      ORDER BY "Id"
                      LIMIT {batchSize}
                    )
                    """;
            }

            return $"""
                UPDATE "OutboxMessages"
                SET "LockedAt" = {now}, "LockedBy" = {instanceId}
                WHERE "Id" IN (
                  SELECT o."Id" FROM "OutboxMessages" o
                  WHERE o."DeliveredAt" IS NULL AND o."LockedAt" IS NULL
                    AND (o."OrderingKey" IS NULL OR NOT EXISTS (
                      SELECT 1 FROM "OutboxMessages" e
                      WHERE e."OrderingKey" = o."OrderingKey" AND e."DeliveredAt" IS NULL AND e."Id" < o."Id"))
                  ORDER BY o."Id"
                  LIMIT {batchSize}
                )
                """;
        }

        public FormattableString GetDueOrderedRetryClaimSql(
            string instanceId,
            DateTimeOffset now,
            DateTimeOffset staleCutoff,
            int batchSize,
            OrderingMode orderingMode)
        {
            Calls.Add(("due", batchSize, staleCutoff));

            if (orderingMode != OrderingMode.PerKey)
            {
                return $"""
                    UPDATE "OutboxMessages"
                    SET "LockedAt" = {now}, "LockedBy" = {instanceId}
                    WHERE "Id" IN (
                      SELECT "Id" FROM "OutboxMessages"
                      WHERE "DeliveredAt" IS NULL AND "LockedAt" IS NOT NULL AND "LockedAt" < {staleCutoff}
                      ORDER BY "LockedAt", "Id"
                      LIMIT {batchSize}
                    )
                    """;
            }

            return $"""
                UPDATE "OutboxMessages"
                SET "LockedAt" = {now}, "LockedBy" = {instanceId}
                WHERE "Id" IN (
                  SELECT o."Id" FROM "OutboxMessages" o
                  WHERE o."DeliveredAt" IS NULL AND o."LockedAt" IS NOT NULL AND o."LockedAt" < {staleCutoff}
                    AND (o."OrderingKey" IS NULL OR NOT EXISTS (
                      SELECT 1 FROM "OutboxMessages" e
                      WHERE e."OrderingKey" = o."OrderingKey" AND e."DeliveredAt" IS NULL AND e."Id" < o."Id"))
                  ORDER BY o."LockedAt", o."Id"
                  LIMIT {batchSize}
                )
                """;
        }
    }
}
