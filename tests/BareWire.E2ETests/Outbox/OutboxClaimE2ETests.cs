using System.Buffers;
using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AwesomeAssertions;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using Microsoft.EntityFrameworkCore;
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
}
