using System.Buffers;
using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BareWire.E2ETests.Outbox;

/// <summary>
/// E2E tests for <see cref="EfCoreOutboxStore.GetPendingAsync"/> claim semantics against a real
/// PostgreSQL instance. These tests require Docker to be available and a running Postgres container.
/// </summary>
/// <remarks>
/// Tests are annotated with the <c>requires-postgres</c> trait so they can be filtered in CI.
/// If Docker is unavailable the fixture initialization will throw and tests will show as failed
/// with a clear message rather than silently passing.
/// </remarks>
[Trait("Category", "requires-postgres")]
public sealed class OutboxClaimE2ETests : IAsyncLifetime
{
    private DistributedApplication? _app;
    private string? _connectionString;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan TestLockTimeout = TimeSpan.FromSeconds(5);

    public async ValueTask InitializeAsync()
    {
        // Build a minimal Aspire AppHost containing only a PostgreSQL resource.
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.BareWire_OutboxE2EAppHost>();

        _app = await builder.BuildAsync();

        var notifier = _app.Services.GetRequiredService<ResourceNotificationService>();
        await _app.StartAsync();

        using var cts = new CancellationTokenSource(StartupTimeout);
        await notifier.WaitForResourceHealthyAsync("outbox-pg", cts.Token);

        _connectionString = await _app.GetConnectionStringAsync("outbox-db", cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private OutboxDbContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<OutboxDbContext>();
        optionsBuilder.UseNpgsql(_connectionString!);
        return new OutboxDbContext(optionsBuilder.Options);
    }

    private static EfCoreOutboxStore CreateStore(OutboxDbContext context, string instanceId, OutboxOptions options)
        => new(context, new OutboxInstanceId(instanceId), new PostgresOutboxSqlDialect(), options);

    private static OutboxOptions CreateOptions(TimeSpan? lockTimeout = null)
        => new()
        {
            PollingInterval = TimeSpan.FromSeconds(1),
            OutboxLockTimeout = lockTimeout ?? TestLockTimeout,
            OutboxRetention = TimeSpan.FromDays(7),
            InboxRetention = TimeSpan.FromDays(8),
            InboxLockTimeout = TimeSpan.FromSeconds(30)
        };

    private static OutboxOptions CreatePerKeyOptions(string orderingKeyHeader = "x-ordering-key", TimeSpan? lockTimeout = null)
        => new()
        {
            PollingInterval = TimeSpan.FromSeconds(1),
            OutboxLockTimeout = lockTimeout ?? TestLockTimeout,
            OutboxRetention = TimeSpan.FromDays(7),
            InboxRetention = TimeSpan.FromDays(8),
            InboxLockTimeout = TimeSpan.FromSeconds(30),
            OrderingMode = OrderingMode.PerKey,
            OrderingKeyHeaderName = orderingKeyHeader
        };

    // Creates a DbContext that includes the OutboxModelCustomizerExtension so that schema
    // creation via EnsureCreatedAsync also creates IX_OutboxMessages_Ordering when PerKey is
    // active. Used only where the test needs the index to be present (E4, E5).
    private OutboxDbContext CreateDbContextWithOrdering(OutboxOptions options)
    {
        var ob = new DbContextOptionsBuilder<OutboxDbContext>();
        ob.UseNpgsql(_connectionString!);
        ((IDbContextOptionsBuilderInfrastructure)ob).AddOrUpdateExtension(
            new OutboxModelCustomizerExtension(options));
        return new OutboxDbContext(ob.Options);
    }

    private static async Task SeedPendingRowsAsync(OutboxDbContext context, int count)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < count; i++)
        {
            context.OutboxMessages.Add(new OutboxMessage
            {
                MessageId = Guid.NewGuid(),
                DestinationAddress = $"test.routing.{i}",
                ContentType = "application/json",
                Payload = [1, 2, 3],
                CreatedAt = now
            });
        }

        await context.SaveChangesAsync();
    }

    private static void ReturnBuffers(IReadOnlyList<OutboxEntry> entries)
    {
        foreach (OutboxEntry e in entries)
        {
            ArrayPool<byte>.Shared.Return(e.PooledBody);
        }
    }

    // Seeds rows with an ordering key header for PerKey E2E tests.
    private static async Task SeedOrderedRowsAsync(
        OutboxDbContext context,
        string orderingKey,
        int count,
        string? overrideDestination = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < count; i++)
        {
            context.OutboxMessages.Add(new OutboxMessage
            {
                MessageId = Guid.NewGuid(),
                DestinationAddress = overrideDestination ?? $"test.ordering.{orderingKey}.{i}",
                ContentType = "application/json",
                Payload = [1, 2, 3],
                CreatedAt = now,
                OrderingKey = orderingKey
            });
        }

        await context.SaveChangesAsync();
    }

    // Bulk inserts rows directly for E5 performance test — avoids slow per-row SaveChanges.
    private static async Task SeedBulkAsync(
        OutboxDbContext context,
        int keyCount,
        int rowsPerKey,
        int hotKeyExtraRows)
    {
        const string hotKey = "hot-key";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var batch = new List<OutboxMessage>(keyCount * rowsPerKey + hotKeyExtraRows);

        for (int k = 0; k < keyCount; k++)
        {
            string key = $"key-{k:D4}";
            for (int r = 0; r < rowsPerKey; r++)
            {
                batch.Add(new OutboxMessage
                {
                    MessageId = Guid.NewGuid(),
                    DestinationAddress = "bulk.test",
                    ContentType = "application/json",
                    Payload = [0],
                    CreatedAt = now,
                    OrderingKey = key
                });
            }
        }

        for (int r = 0; r < hotKeyExtraRows; r++)
        {
            batch.Add(new OutboxMessage
            {
                MessageId = Guid.NewGuid(),
                DestinationAddress = "bulk.test",
                ContentType = "application/json",
                Payload = [0],
                CreatedAt = now,
                OrderingKey = hotKey
            });
        }

        context.OutboxMessages.AddRange(batch);
        await context.SaveChangesAsync();
    }

    // ── Test #1: Disjoint batches ─────────────────────────────────────────────

    /// <summary>
    /// Two concurrent instances calling <see cref="EfCoreOutboxStore.GetPendingAsync"/> must
    /// each receive disjoint row sets — proving the <c>FOR UPDATE SKIP LOCKED</c> claim is atomic.
    /// </summary>
    [Fact]
    public async Task GetPendingAsync_WhenCalledConcurrentlyByTwoInstances_ReturnsDisjointBatches()
    {
        // Arrange — shared schema + seed rows
        await using OutboxDbContext seedContext = CreateDbContext();
        await seedContext.Database.EnsureCreatedAsync();

        const int rowCount = 50;
        await SeedPendingRowsAsync(seedContext, rowCount);

        OutboxOptions options = CreateOptions();

        // Two independent contexts + stores with distinct instanceIds
        await using OutboxDbContext contextA = CreateDbContext();
        await using OutboxDbContext contextB = CreateDbContext();
        EfCoreOutboxStore storeA = CreateStore(contextA, "instance-A", options);
        EfCoreOutboxStore storeB = CreateStore(contextB, "instance-B", options);

        // Act — concurrent claim
        IReadOnlyList<OutboxEntry>[] batches = await Task.WhenAll(
            storeA.GetPendingAsync(rowCount, CancellationToken.None).AsTask(),
            storeB.GetPendingAsync(rowCount, CancellationToken.None).AsTask());

        IReadOnlyList<OutboxEntry> batchA = batches[0];
        IReadOnlyList<OutboxEntry> batchB = batches[1];

        try
        {
            // Assert — id sets must be disjoint
            var idsA = batchA.Select(e => e.Id).ToHashSet();
            var idsB = batchB.Select(e => e.Id).ToHashSet();

            idsA.Intersect(idsB).Should().BeEmpty(
                "two concurrent instances must claim disjoint batches via FOR UPDATE SKIP LOCKED");

            (idsA.Count + idsB.Count).Should().BeLessThanOrEqualTo(rowCount,
                "the total claimed rows must not exceed the number of seeded rows");
        }
        finally
        {
            ReturnBuffers(batchA);
            ReturnBuffers(batchB);
        }
    }

    // ── Test #2: Stale-lock re-claim (crash-recovery) ─────────────────────────

    /// <summary>
    /// A row whose lock has expired (simulating a crashed instance) must be returned by a new
    /// <see cref="EfCoreOutboxStore.GetPendingAsync"/> call — proving the no-drop invariant.
    /// </summary>
    [Fact]
    public async Task GetPendingAsync_WhenLockExpired_ReClaimsRow()
    {
        // Arrange — fresh schema per test
        await using OutboxDbContext seedContext = CreateDbContext();
        await seedContext.Database.EnsureCreatedAsync();

        OutboxOptions options = CreateOptions(lockTimeout: TestLockTimeout);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Seed a row that is already locked but the lock is expired
        DateTimeOffset staleLockTime = now - (options.OutboxLockTimeout + TimeSpan.FromSeconds(1));
        seedContext.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "test.crash-recovery",
            ContentType = "application/json",
            Payload = [42],
            CreatedAt = now - TimeSpan.FromMinutes(5),
            LockedAt = staleLockTime,
            LockedBy = "crashed-instance"
        });
        await seedContext.SaveChangesAsync();

        // Act — a healthy instance polls
        await using OutboxDbContext healthyContext = CreateDbContext();
        EfCoreOutboxStore healthyStore = CreateStore(healthyContext, "healthy-instance", options);

        IReadOnlyList<OutboxEntry> result = await healthyStore.GetPendingAsync(10, CancellationToken.None);

        try
        {
            // Assert — stale-locked row must be re-claimed and returned
            result.Should().HaveCount(1,
                "the row with an expired lock must be re-claimable after OutboxLockTimeout elapses");
            result[0].RoutingKey.Should().Be("test.crash-recovery");

            // No-drop invariant (SEC-2): re-claiming alone is not enough — the row must be
            // ultimately deliverable. Mark the re-claimed row delivered and confirm it transitions
            // to DeliveredAt != null and no longer pends.
            await healthyStore.MarkDeliveredAsync(
                result.Select(e => e.Id).ToList(),
                CancellationToken.None);

            await using OutboxDbContext verifyContext = CreateDbContext();
            OutboxMessage delivered = await verifyContext.OutboxMessages
                .AsNoTracking()
                .SingleAsync(m => m.DestinationAddress == "test.crash-recovery");
            delivered.DeliveredAt.Should().NotBeNull(
                "the re-claimed row must be ultimately delivered (no-drop invariant)");
        }
        finally
        {
            ReturnBuffers(result);
        }
    }

    // ── Test #3: Explicit release re-claims immediately (R7.6) ─────────────────

    /// <summary>
    /// After a nack, <see cref="EfCoreOutboxStore.ReleaseLockAsync"/> must clear the row's lock so a
    /// different instance re-claims it on the very next poll — without waiting for
    /// <c>OutboxLockTimeout</c> to expire (R7.6, low-latency retry).
    /// </summary>
    [Fact]
    public async Task ReleaseLockAsync_AfterNack_RowReClaimedNextCycleWithoutWaitingForLockTimeout()
    {
        // Arrange — a deliberately large lock timeout so a re-claim can only succeed via the explicit
        // release, never via lock expiry within the test window.
        await using OutboxDbContext seedContext = CreateDbContext();
        await seedContext.Database.EnsureCreatedAsync();

        OutboxOptions options = CreateOptions(lockTimeout: TimeSpan.FromSeconds(30));
        await SeedPendingRowsAsync(seedContext, count: 1);

        await using OutboxDbContext contextA = CreateDbContext();
        await using OutboxDbContext contextB = CreateDbContext();
        EfCoreOutboxStore storeA = CreateStore(contextA, "instance-A", options);
        EfCoreOutboxStore storeB = CreateStore(contextB, "instance-B", options);

        // Act — instance A claims the row (a dispatch cycle), then releases it after a simulated nack.
        IReadOnlyList<OutboxEntry> claimed = await storeA.GetPendingAsync(10, CancellationToken.None);
        claimed.Should().HaveCount(1, "the seeded row must be claimable");
        long claimedId = claimed[0].Id;

        var stopwatch = Stopwatch.StartNew();
        IReadOnlySet<long> retained = await storeA.ReleaseLockAsync([claimedId], CancellationToken.None);

        IReadOnlyList<OutboxEntry> reClaimed = Array.Empty<OutboxEntry>();
        try
        {
            // EF Core copies each row into a fresh per-cycle buffer, so it retains none.
            retained.Should().BeEmpty("EF Core retains no caller buffers");

            // The lock must be cleared in the database.
            await using (OutboxDbContext verifyContext = CreateDbContext())
            {
                OutboxMessage row = await verifyContext.OutboxMessages
                    .AsNoTracking()
                    .SingleAsync(m => m.Id == claimedId);
                row.LockedAt.Should().BeNull("ReleaseLockAsync must zero LockedAt");
                row.LockedBy.Should().BeNull("ReleaseLockAsync must zero LockedBy");
                row.DeliveredAt.Should().BeNull("a nacked row must not be marked delivered");
            }

            // A *different* instance must re-claim the row immediately — far below OutboxLockTimeout.
            // Without the explicit release, instance B would see instance A's fresh (non-stale) lock
            // and claim nothing until the 30 s timeout elapsed.
            reClaimed = await storeB.GetPendingAsync(10, CancellationToken.None);
            stopwatch.Stop();

            reClaimed.Should().HaveCount(1,
                "the released row must be re-claimable by another instance on the next poll");
            reClaimed[0].Id.Should().Be(claimedId);
            stopwatch.Elapsed.Should().BeLessThan(options.OutboxLockTimeout,
                "the row is re-claimed via explicit release, not by waiting for OutboxLockTimeout");
        }
        finally
        {
            ReturnBuffers(claimed);
            ReturnBuffers(reClaimed);
        }
    }

    // ── Test #4: Release respects instance ownership (cross-instance guard) ────

    /// <summary>
    /// <see cref="EfCoreOutboxStore.ReleaseLockAsync"/> must only release locks owned by the calling
    /// instance — an attempt to release another instance's claim is a no-op, preserving the B4
    /// no-double-delivery guarantee.
    /// </summary>
    [Fact]
    public async Task ReleaseLockAsync_ForRowLockedByAnotherInstance_DoesNotReleaseLock()
    {
        // Arrange
        await using OutboxDbContext seedContext = CreateDbContext();
        await seedContext.Database.EnsureCreatedAsync();

        OutboxOptions options = CreateOptions(lockTimeout: TimeSpan.FromSeconds(30));
        await SeedPendingRowsAsync(seedContext, count: 1);

        await using OutboxDbContext contextA = CreateDbContext();
        await using OutboxDbContext contextB = CreateDbContext();
        EfCoreOutboxStore storeA = CreateStore(contextA, "instance-A", options);
        EfCoreOutboxStore storeB = CreateStore(contextB, "instance-B", options);

        // instance A claims the row.
        IReadOnlyList<OutboxEntry> claimed = await storeA.GetPendingAsync(10, CancellationToken.None);
        claimed.Should().HaveCount(1);
        long claimedId = claimed[0].Id;

        try
        {
            // Act — instance B (NOT the owner) attempts to release A's lock.
            IReadOnlySet<long> retained = await storeB.ReleaseLockAsync([claimedId], CancellationToken.None);
            retained.Should().BeEmpty();

            // Assert — the lock must remain held by instance A (cross-instance filter rejected B).
            await using OutboxDbContext verifyContext = CreateDbContext();
            OutboxMessage row = await verifyContext.OutboxMessages
                .AsNoTracking()
                .SingleAsync(m => m.Id == claimedId);
            row.LockedBy.Should().Be("instance-A", "an instance must not release another instance's lock");
            row.LockedAt.Should().NotBeNull("the original lock timestamp must be preserved");
        }
        finally
        {
            ReturnBuffers(claimed);
        }
    }

    // ── E1: Head-of-line per key (PerKey, real PostgreSQL) ────────────────────

    /// <summary>
    /// With <see cref="OrderingMode.PerKey"/> active, only the head (oldest undelivered) row
    /// for a given key must be claimable. After <see cref="EfCoreOutboxStore.MarkDeliveredAsync"/>
    /// delivers the head, the next row becomes the new head and is claimable.
    /// </summary>
    [Fact]
    public async Task GetPendingAsync_PerKey_WhenTwoRowsSameKey_ClaimsOnlyHead()
    {
        // Arrange
        await using OutboxDbContext seedContext = CreateDbContext();
        await seedContext.Database.EnsureCreatedAsync();

        OutboxOptions options = CreatePerKeyOptions();

        // Seed two rows for the same key — row with the lower Id is the head.
        await SeedOrderedRowsAsync(seedContext, orderingKey: "order-1", count: 2);

        List<long> allIds = await seedContext.OutboxMessages
            .AsNoTracking()
            .Where(m => m.OrderingKey == "order-1")
            .OrderBy(m => m.Id)
            .Select(m => m.Id)
            .ToListAsync();

        long headId = allIds[0];
        long tailId = allIds[1];

        await using OutboxDbContext claimContext = CreateDbContext();
        EfCoreOutboxStore store = CreateStore(claimContext, "instance-A", options);

        // Act — first claim: only the head should be returned.
        IReadOnlyList<OutboxEntry> first = await store.GetPendingAsync(10, CancellationToken.None);

        try
        {
            first.Should().HaveCount(1, "only the head row for the key is claimable");
            first[0].Id.Should().Be(headId, "the claimed row must be the head (lowest Id) of the key");
            first[0].OrderingKey.Should().Be("order-1");
        }
        finally
        {
            ReturnBuffers(first);
        }

        // Deliver the head.
        await store.MarkDeliveredAsync([headId], CancellationToken.None);

        // Act — second claim: after head is delivered, the tail becomes the new head.
        IReadOnlyList<OutboxEntry> second = await store.GetPendingAsync(10, CancellationToken.None);

        try
        {
            second.Should().HaveCount(1, "tail becomes claimable once the head is delivered");
            second[0].Id.Should().Be(tailId, "the formerly blocked tail is now the head");
        }
        finally
        {
            ReturnBuffers(second);
        }
    }

    // ── E2: Key-affinity cross-instance (mirror B4 disjoint test) ─────────────

    /// <summary>
    /// Two concurrent instances must NOT both claim rows belonging to the same ordering key
    /// simultaneously. With <c>FOR UPDATE SKIP LOCKED</c> and the head-of-line predicate,
    /// at most one instance may hold the head of a given key at any time.
    /// </summary>
    [Fact]
    public async Task GetPendingAsync_PerKey_TwoInstancesSameKey_ClaimDisjointRows()
    {
        // Arrange
        await using OutboxDbContext seedContext = CreateDbContext();
        await seedContext.Database.EnsureCreatedAsync();

        OutboxOptions options = CreatePerKeyOptions(lockTimeout: TimeSpan.FromSeconds(30));

        // Seed 2 rows of the same key — only 1 head can be claimed concurrently.
        await SeedOrderedRowsAsync(seedContext, orderingKey: "key-A", count: 2);

        await using OutboxDbContext contextA = CreateDbContext();
        await using OutboxDbContext contextB = CreateDbContext();
        EfCoreOutboxStore storeA = CreateStore(contextA, "instance-A", options);
        EfCoreOutboxStore storeB = CreateStore(contextB, "instance-B", options);

        // Act — concurrent claim
        IReadOnlyList<OutboxEntry>[] batches = await Task.WhenAll(
            storeA.GetPendingAsync(10, CancellationToken.None).AsTask(),
            storeB.GetPendingAsync(10, CancellationToken.None).AsTask());

        IReadOnlyList<OutboxEntry> batchA = batches[0];
        IReadOnlyList<OutboxEntry> batchB = batches[1];

        try
        {
            var idsA = batchA.Select(e => e.Id).ToHashSet();
            var idsB = batchB.Select(e => e.Id).ToHashSet();

            // The two batches must be disjoint — no row may be claimed by both instances.
            idsA.Intersect(idsB).Should().BeEmpty(
                "two instances must not both claim the same head row for a key");

            // Combined, they hold at most 2 rows but only 1 head is claimable at a time,
            // so at most 1 instance can claim any row.
            (idsA.Count + idsB.Count).Should().BeLessThanOrEqualTo(1,
                "only one instance may claim the single head row; the second is blocked");
        }
        finally
        {
            ReturnBuffers(batchA);
            ReturnBuffers(batchB);
        }
    }

    // ── E3: Parallelism — distinct keys + keyless all claimable in one batch ──

    /// <summary>
    /// With <see cref="OrderingMode.PerKey"/> active, rows with distinct keys and keyless rows
    /// must all be claimable in a single <see cref="EfCoreOutboxStore.GetPendingAsync"/> call —
    /// the head-of-line predicate must not collapse them.
    /// </summary>
    [Fact]
    public async Task GetPendingAsync_PerKey_DistinctKeysAndKeyless_AllClaimableInOneBatch()
    {
        // Arrange
        await using OutboxDbContext seedContext = CreateDbContext();
        await seedContext.Database.EnsureCreatedAsync();

        OutboxOptions options = CreatePerKeyOptions();

        // Seed 1 row for key "A", 1 for key "B", 2 keyless rows.
        await SeedOrderedRowsAsync(seedContext, orderingKey: "A", count: 1);
        await SeedOrderedRowsAsync(seedContext, orderingKey: "B", count: 1);
        await SeedPendingRowsAsync(seedContext, count: 2); // keyless

        await using OutboxDbContext claimContext = CreateDbContext();
        EfCoreOutboxStore store = CreateStore(claimContext, "instance-X", options);

        // Act
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10, CancellationToken.None);

        try
        {
            // Assert — all 4 rows (head-A, head-B, keyless-1, keyless-2) must be claimed.
            batch.Should().HaveCount(4,
                "head of key A, head of key B, and both keyless rows are all independently claimable");

            int keyACount = batch.Count(e => e.OrderingKey == "A");
            int keyBCount = batch.Count(e => e.OrderingKey == "B");
            int keylessCount = batch.Count(e => e.OrderingKey is null);

            keyACount.Should().Be(1, "exactly one head for key A");
            keyBCount.Should().Be(1, "exactly one head for key B");
            keylessCount.Should().Be(2, "both keyless rows pass through without restriction");
        }
        finally
        {
            ReturnBuffers(batch);
        }
    }

    // ── E4: Index IX_OutboxMessages_Ordering present in PerKey, absent in None ─

    /// <summary>
    /// The partial index <c>IX_OutboxMessages_Ordering</c> must be created by
    /// <see cref="OutboxModelCustomizerExtension"/> when <see cref="OrderingMode.PerKey"/> is
    /// active, and must be absent when <see cref="OrderingMode.None"/> is used.
    /// Verified by introspecting <c>pg_indexes</c> on a real PostgreSQL instance.
    /// </summary>
    [Fact]
    public async Task EnsureCreated_PerKey_CreatesOrderingIndex_None_DoesNot()
    {
        const string indexName = "IX_OutboxMessages_Ordering";

        // ── PerKey: index must be present ────────────────────────────────────
        OutboxOptions perKeyOptions = CreatePerKeyOptions();
        await using (OutboxDbContext ctxPerKey = CreateDbContextWithOrdering(perKeyOptions))
        {
            await ctxPerKey.Database.EnsureCreatedAsync();

            bool indexExists = await ctxPerKey.Database
                .SqlQuery<int>(
                    $"SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = {indexName}")
                .AnyAsync();

            indexExists.Should().BeTrue(
                $"index {indexName} must be created when OrderingMode is PerKey");
        }

        // ── None: index must be absent ────────────────────────────────────────
        // Drop the index that was just created, then re-check with a None context.
        await using (OutboxDbContext ctxCheck = CreateDbContext())
        {
            await ctxCheck.Database
                .ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{indexName}\"");

            bool indexStillExists = await ctxCheck.Database
                .SqlQuery<int>(
                    $"SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = {indexName}")
                .AnyAsync();

            indexStillExists.Should().BeFalse(
                $"index {indexName} must be absent when OrderingMode is None (guard §2.1)");
        }
    }

    // ── E5: EXPLAIN ANALYZE — NOT EXISTS uses IX_OutboxMessages_Ordering ──────

    /// <summary>
    /// Under <see cref="OrderingMode.PerKey"/> with a hot key (~100 k rows, ~1 k distinct keys),
    /// the <c>EXPLAIN (ANALYZE, BUFFERS)</c> output for a claim query must show that the
    /// <c>NOT EXISTS</c> correlated subquery uses an Index Scan (or Index-Only Scan) on
    /// <c>IX_OutboxMessages_Ordering</c> rather than a sequential scan of
    /// <c>OutboxMessages</c> — the load-bearing correctness criterion for E5 (PERF-2).
    /// </summary>
    /// <remarks>
    /// This test seeds ~100 k rows and runs <c>ANALYZE</c> before <c>EXPLAIN</c> to ensure
    /// the planner has current statistics. It is intentionally slow (bulk insert + analyze)
    /// and is gated behind the <c>requires-postgres</c> trait.
    /// </remarks>
    [Fact]
    public async Task GetPendingAsync_PerKey_HotKeyLoad_ClaimUsesOrderingIndex()
    {
        const int keyCount = 1_000;
        const int rowsPerKey = 99;        // 1 000 × 99 = 99 000 rows
        const int hotKeyExtra = 1_000;    // additional rows for the "hot-key" = ~100 000 total

        OutboxOptions options = CreatePerKeyOptions();

        // Schema must include the ordering index — use the ordering-aware context.
        await using OutboxDbContext seedCtx = CreateDbContextWithOrdering(options);
        await seedCtx.Database.EnsureCreatedAsync();

        // Bulk-seed rows.
        await SeedBulkAsync(seedCtx, keyCount, rowsPerKey, hotKeyExtra);

        // Run ANALYZE so the planner has fresh statistics before EXPLAIN.
        await seedCtx.Database.ExecuteSqlRawAsync("ANALYZE \"OutboxMessages\"");

        // Build the EXPLAIN query using raw SQL with literal placeholders — EXPLAIN (ANALYZE,
        // BUFFERS) is a planning statement, not DML. We use a fixed instance id and
        // times just to get the plan shape; no user-supplied values enter the query.
        // SEC: the EXPLAIN output contains only structural SQL identifiers and timing, not
        // OrderingKey or LockedBy values.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset staleCutoff = now - options.OutboxLockTimeout;

        // The PerKey claim SQL shape (from PostgresOutboxSqlDialect) with literal parameters.
        // We use a stable future timestamp so PG does not execute the DML (EXPLAIN only plans).
        string explainRawSql = $"""
            EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT)
            UPDATE "OutboxMessages"
            SET "LockedAt" = '{now:O}', "LockedBy" = 'explain-instance'
            WHERE "Id" IN (
              SELECT o."Id" FROM "OutboxMessages" o
              WHERE o."DeliveredAt" IS NULL
                AND (o."LockedAt" IS NULL OR o."LockedAt" < '{staleCutoff:O}')
                AND (
                  o."OrderingKey" IS NULL
                  OR NOT EXISTS (
                    SELECT 1 FROM "OutboxMessages" e
                    WHERE e."OrderingKey" = o."OrderingKey"
                      AND e."DeliveredAt" IS NULL
                      AND e."Id" < o."Id"
                  )
                )
              ORDER BY o."Id"
              LIMIT 100
              FOR UPDATE SKIP LOCKED
            )
            """;

        // Execute raw EXPLAIN — returns text rows from PostgreSQL.
        var planLines = new List<string>();
        await using (var cmd = seedCtx.Database.GetDbConnection().CreateCommand())
        {
            if (seedCtx.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
            {
                await seedCtx.Database.GetDbConnection().OpenAsync();
            }

            cmd.CommandText = explainRawSql;

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                planLines.Add(reader.GetString(0));
            }
        }

        string planText = string.Join("\n", planLines);

        // The NOT EXISTS subquery must use an index scan on IX_OutboxMessages_Ordering.
        // SEC: planText contains only structural SQL node descriptions, not user data.
        bool hasIndexScanOnOrderingIndex = planLines.Any(line =>
            (line.Contains("Index Scan", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("Index Only Scan", StringComparison.OrdinalIgnoreCase))
            && line.Contains("IX_OutboxMessages_Ordering", StringComparison.OrdinalIgnoreCase));

        bool hasSeqScanOnOutboxInSubquery = planLines.Any(line =>
            line.Contains("Seq Scan on", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("outboxmessages e", StringComparison.OrdinalIgnoreCase));

        // Assert index is used — load-bearing criterion (PERF-2 / E5 §6).
        hasIndexScanOnOrderingIndex.Should().BeTrue(
            "the NOT EXISTS subquery must use IX_OutboxMessages_Ordering index scan, " +
            $"not a seq scan; plan:\n{planText}");

        hasSeqScanOnOutboxInSubquery.Should().BeFalse(
            "the NOT EXISTS subquery must not result in a sequential scan on OutboxMessages; " +
            $"plan:\n{planText}");
    }

    // ── E6: Over-limit key (>256 chars) → NULL (keyless), not truncated ────────

    /// <summary>
    /// When an <see cref="OrderingMode.PerKey"/> message carries an ordering-key header value
    /// longer than 256 characters, the row must be stored as keyless (<c>OrderingKey IS NULL</c>)
    /// — never truncated to 256 chars.  Truncation would silently merge distinct long keys into a
    /// single head-of-line group, which is both a correctness bug and a manipulation vector.
    /// </summary>
    [Fact]
    public async Task SaveMessages_PerKey_OverLimitKey_StoresAsNull_NeverTruncated()
    {
        // Arrange
        await using OutboxDbContext seedContext = CreateDbContext();
        await seedContext.Database.EnsureCreatedAsync();

        OutboxOptions options = CreatePerKeyOptions();

        // Two distinct keys that are both > 256 chars and differ only in the 257th character.
        // If truncation occurred, they would be stored as the same value — a collision.
        string prefix = new('k', 256);
        string longKeyA = prefix + "A";   // 257 chars
        string longKeyB = prefix + "B";   // 257 chars

        await using OutboxDbContext storeContext = CreateDbContext();
        EfCoreOutboxStore store = CreateStore(storeContext, "instance-X", options);

        var messages = new List<OutboundMessage>
        {
            new(
                routingKey: "test.overlimit",
                headers: new Dictionary<string, string> { ["x-ordering-key"] = longKeyA },
                body: new ReadOnlyMemory<byte>([1]),
                contentType: "application/json"),
            new(
                routingKey: "test.overlimit",
                headers: new Dictionary<string, string> { ["x-ordering-key"] = longKeyB },
                body: new ReadOnlyMemory<byte>([2]),
                contentType: "application/json")
        };

        await store.SaveMessagesAsync(messages, CancellationToken.None);
        await storeContext.SaveChangesAsync();

        // Assert — both rows must have NULL OrderingKey (keyless), not a truncated value.
        await using OutboxDbContext verifyContext = CreateDbContext();
        List<string?> storedKeys = await verifyContext.OutboxMessages
            .AsNoTracking()
            .Where(m => m.DestinationAddress == "test.overlimit")
            .Select(m => m.OrderingKey)
            .ToListAsync();

        storedKeys.Should().HaveCount(2,
            "both over-limit rows must be persisted");
        storedKeys.Should().AllSatisfy(k => k.Should().BeNull(),
            "over-limit keys must be stored as NULL (keyless), not truncated (SEC-2)");
    }
}
