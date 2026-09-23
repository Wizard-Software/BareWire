using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Outbox;

// EfCoreOutboxStore.GetOldestDueRetryAsync (IOutboxRetryBacklogProbe) on SQLite — the client-side
// fallback branch, since SQLite never matches PostgresOutboxSqlDialect.ProviderName. The retry-backlog
// class covers deferred nacks AND abandoned (stale) claims: any still-locked, undelivered row whose
// LockedAt is older than the stale-lock cutoff.
public sealed class EfCoreOutboxStoreRetryBacklogProbeTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly OutboxOptions _options = new();
    private SqliteConnection _connection = null!;
    private OutboxDbContext _dbContext = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _dbContext = new OutboxDbContext(ContextOptions().Options);
        await _dbContext.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task GetOldestDueRetryAsync_NackedRowNotYetDue_ReturnsNull()
    {
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = (await SaveAndClaim(storeA, 1)).Single();

        await storeA.ReleaseLockAsync([id], []);

        DateTimeOffset? result = await storeA.GetOldestDueRetryAsync(clock.GetUtcNow());

        result.Should().BeNull("the row was just released — LockedAt is not yet older than the stale-lock cutoff");
    }

    [Fact]
    public async Task GetOldestDueRetryAsync_NackedRowDue_ReturnsLockedAtPlusLockTimeout()
    {
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = (await SaveAndClaim(storeA, 1)).Single();

        await storeA.ReleaseLockAsync([id], []);
        clock.Advance(_options.OutboxLockTimeout + _options.OutboxLockTimeout);
        DateTimeOffset now = clock.GetUtcNow();

        DateTimeOffset? result = await storeA.GetOldestDueRetryAsync(now);

        OutboxMessage row = await Row(id);
        result.Should().Be(row.LockedAt!.Value + _options.OutboxLockTimeout);
        result!.Value.Should().BeBefore(now);
    }

    [Fact]
    public async Task GetOldestDueRetryAsync_BarrierReleasedAndStaleLockedRows_AreNotCounted()
    {
        // Arrange — one barrier-released row (LockedAt == null, excluded by the filter itself) and one
        // row claimed by another instance with a stale lock (LockedAt older than the cutoff) — the stale
        // claim IS part of the retry-backlog class (abandoned claim), so it must be counted, not excluded.
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long barrierReleased = (await SaveAndClaim(storeA, 1)).Single();

        // Claim storeB's own row BEFORE releasing storeA's barrier row, so storeB's claim cannot also
        // pick up the now-unclaimed barrier row (both would otherwise be claimable in one batch).
        EfCoreOutboxStore storeB = Store("b", clock);
        long staleClaim = (await SaveAndClaim(storeB, 1)).Single();

        await storeA.ReleaseLockAsync([], [barrierReleased]);

        clock.Advance(_options.OutboxLockTimeout + _options.OutboxLockTimeout);
        DateTimeOffset now = clock.GetUtcNow();

        DateTimeOffset? result = await storeA.GetOldestDueRetryAsync(now);

        OutboxMessage staleRow = await Row(staleClaim);
        result.Should().Be(staleRow.LockedAt!.Value + _options.OutboxLockTimeout,
            "the stale claim (abandoned by instance b) is part of the retry-backlog class; the barrier-released row has LockedAt == null and is excluded");
    }

    [Fact]
    public async Task GetOldestDueRetryAsync_FreshInFlightClaim_IsNotCounted()
    {
        // Arrange — a row claimed just now (in-flight) is locked but not yet due; only a due row should
        // be reported.
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        await SaveAndClaim(storeA, 1);

        DateTimeOffset? result = await storeA.GetOldestDueRetryAsync(clock.GetUtcNow());

        result.Should().BeNull("a fresh in-flight claim is not yet due");
    }

    [Fact]
    public async Task GetPendingAsync_NackedRowReclaimed_CarriesRetryCountAsNackCount()
    {
        // Arrange — nack twice, advancing past each deferral, then reclaim a third time.
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = (await SaveAndClaim(storeA, 1)).Single();

        await storeA.ReleaseLockAsync([id], []);
        clock.Advance(_options.OutboxLockTimeout + _options.OutboxLockTimeout);
        IReadOnlyList<OutboxEntry> firstReclaim = await storeA.GetPendingAsync(10);
        ReturnBuffers(firstReclaim);
        firstReclaim.Should().ContainSingle();

        await storeA.ReleaseLockAsync([id], []);
        clock.Advance(_options.OutboxLockTimeout + _options.OutboxLockTimeout);

        IReadOnlyList<OutboxEntry> secondReclaim = await storeA.GetPendingAsync(10);
        ReturnBuffers(secondReclaim);

        secondReclaim.Should().ContainSingle().Which.NackCount.Should().Be(2);
    }

    private DbContextOptionsBuilder<OutboxDbContext> ContextOptions()
        => new DbContextOptionsBuilder<OutboxDbContext>().UseSqlite(_connection);

    private EfCoreOutboxStore Store(string instance, TimeProvider clock)
        => new(
            _dbContext,
            new OutboxInstanceId(instance),
            new PostgresOutboxSqlDialect(),
            _options,
            clock,
            new FixedJitterSource(0.5));

    private async Task<List<long>> SaveAndClaim(EfCoreOutboxStore store, int count)
    {
        await store.SaveMessagesAsync([.. Enumerable.Range(0, count).Select(_ => Message())]);
        await _dbContext.SaveChangesAsync();

        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);
        ReturnBuffers(batch);
        batch.Should().HaveCount(count);

        return [.. batch.Select(e => e.Id)];
    }

    private Task<OutboxMessage> Row(long id)
        => _dbContext.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == id);

    private static void ReturnBuffers(IReadOnlyList<OutboxEntry> batch)
    {
        foreach (OutboxEntry entry in batch)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(entry.PooledBody);
        }
    }

    private static OutboundMessage Message()
        => new(
            routingKey: "orders.created",
            headers: new Dictionary<string, string>(),
            body: "{}"u8.ToArray(),
            contentType: "application/json");

    private sealed class FixedJitterSource(double value) : IOutboxJitterSource
    {
        public double NextDouble() => value;
    }
}
