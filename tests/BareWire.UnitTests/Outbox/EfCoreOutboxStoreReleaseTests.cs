using System.Data.Common;
using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Outbox;

// Drives EfCoreOutboxStore.ReleaseLockAsync against SQLite with an injected FakeTimeProvider and a
// fixed jitter source. SQLite does not match the PostgreSQL dialect's provider name, so claims take
// the client-side fallback path, which compares LockedAt with the stale-lock cutoff in memory — the
// same "not claimable before" predicate a deferred nack relies on.
public sealed class EfCoreOutboxStoreReleaseTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const double Jitter = 0.5;

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
    public async Task ReleaseLockAsync_Nack_DefersRowUntilBackoffElapsed()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = (await SaveAndClaim(storeA, 1)).Single();
        TimeSpan deferral = ExpectedDeferral(0, id);

        // Act
        IReadOnlySet<long> retained = await storeA.ReleaseLockAsync([id], []);

        // Assert — the row is released, counted as a retry, and stamped "not claimable before".
        retained.Should().BeEmpty();
        OutboxMessage row = await Row(id);
        row.LockedBy.Should().BeNull();
        row.RetryCount.Should().Be(1);
        row.LockedAt.Should().Be(T0 - _options.OutboxLockTimeout + deferral);
        row.LockedAt!.Value.Offset.Should().Be(TimeSpan.Zero);

        // Just before the deferral elapses no instance can claim the row.
        clock.Advance(deferral - TimeSpan.FromMilliseconds(1));
        IReadOnlyList<OutboxEntry> early = await Store("b", clock).GetPendingAsync(10);
        ReturnBuffers(early);
        early.Should().BeEmpty();

        // Just after it elapses the row is claimable again.
        clock.Advance(TimeSpan.FromMilliseconds(2));
        IReadOnlyList<OutboxEntry> late = await Store("b", clock).GetPendingAsync(10);
        ReturnBuffers(late);
        late.Should().ContainSingle().Which.Id.Should().Be(id);
        (await Row(id)).LockedBy.Should().Be("b");
    }

    [Fact]
    public async Task ReleaseLockAsync_SecondNack_EscalatesDeferralAndIncrementsRetryCount()
    {
        // Arrange — first claim and nack, then reclaim once the first deferral has elapsed.
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = (await SaveAndClaim(storeA, 1)).Single();
        await storeA.ReleaseLockAsync([id], []);
        clock.Advance(ExpectedDeferral(0, id) + TimeSpan.FromMilliseconds(1));
        IReadOnlyList<OutboxEntry> reclaimed = await storeA.GetPendingAsync(10);
        ReturnBuffers(reclaimed);
        reclaimed.Should().ContainSingle().Which.Id.Should().Be(id);
        DateTimeOffset secondNackAt = clock.GetUtcNow();

        // Act
        await storeA.ReleaseLockAsync([id], []);

        // Assert
        ExpectedDeferral(1, id).Should().BeGreaterThan(ExpectedDeferral(0, id));
        OutboxMessage row = await Row(id);
        row.RetryCount.Should().Be(2);
        row.LockedBy.Should().BeNull();
        row.LockedAt.Should().Be(secondNackAt - _options.OutboxLockTimeout + ExpectedDeferral(1, id));
    }

    [Fact]
    public async Task ReleaseLockAsync_NackedTogether_EachRowGetsItsJitterBucketDeferral()
    {
        // Arrange — four rows whose ids cover every jitter bucket (Id % 4 == 0..3).
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        List<long> ids = await SaveAndClaim(storeA, 4);
        ids.Select(id => id % OutboxNackDeferralSchedule.JitterBucketCount)
            .Should().BeEquivalentTo([0L, 1L, 2L, 3L]);

        // Act
        await storeA.ReleaseLockAsync(ids, []);

        // Assert
        var lockedAts = new List<DateTimeOffset?>();
        foreach (long id in ids)
        {
            OutboxMessage row = await Row(id);
            row.LockedAt.Should().Be(T0 - _options.OutboxLockTimeout + ExpectedDeferral(0, id));
            row.RetryCount.Should().Be(1);
            lockedAts.Add(row.LockedAt);
        }

        lockedAts.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ReleaseLockAsync_RetryCountAtEscalationCap_UsesCapBucket()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = (await SaveAndClaim(storeA, 1)).Single();
        await SetRetryCount(id, 1000);

        // Act
        await storeA.ReleaseLockAsync([id], []);

        // Assert
        OutboxMessage row = await Row(id);
        row.RetryCount.Should().Be(1001);
        row.LockedAt.Should().Be(T0 - _options.OutboxLockTimeout + ExpectedDeferral(1000, id));
    }

    [Fact]
    public async Task ReleaseLockAsync_RetryCountAtIntMax_StaysAtIntMaxAndUsesCapBucket()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = (await SaveAndClaim(storeA, 1)).Single();
        await SetRetryCount(id, int.MaxValue);
        TimeSpan deferral = ExpectedDeferral(int.MaxValue, id);

        // Act
        await storeA.ReleaseLockAsync([id], []);

        // Assert — the counter saturates instead of overflowing, so the row stays readable.
        OutboxMessage row = await Row(id);
        row.RetryCount.Should().Be(int.MaxValue);
        row.LockedAt.Should().Be(T0 - _options.OutboxLockTimeout + deferral);

        clock.Advance(deferral + TimeSpan.FromMilliseconds(1));
        IReadOnlyList<OutboxEntry> batch = await Store("b", clock).GetPendingAsync(10);
        ReturnBuffers(batch);
        batch.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task ReleaseLockAsync_Barrier_ReleasesImmediatelyWithoutDeferralOrIncrement()
    {
        // Arrange — one row rejected by the transport, one held back only by the ordering barrier.
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        List<long> ids = await SaveAndClaim(storeA, 2);
        long nacked = ids[0];
        long barrierReleased = ids[1];

        // Act
        await storeA.ReleaseLockAsync([nacked], [barrierReleased]);

        // Assert — the barrier row carries no deferral and no retry.
        OutboxMessage barrierRow = await Row(barrierReleased);
        barrierRow.LockedAt.Should().BeNull();
        barrierRow.LockedBy.Should().BeNull();
        barrierRow.RetryCount.Should().Be(0);

        OutboxMessage nackedRow = await Row(nacked);
        nackedRow.RetryCount.Should().Be(1);
        nackedRow.LockedAt.Should().Be(T0 - _options.OutboxLockTimeout + ExpectedDeferral(0, nacked));

        // Without advancing the clock only the barrier row is claimable again.
        IReadOnlyList<OutboxEntry> batch = await Store("b", clock).GetPendingAsync(10);
        ReturnBuffers(batch);
        batch.Should().ContainSingle().Which.Id.Should().Be(barrierReleased);
    }

    [Fact]
    public async Task ReleaseLockAsync_BarrierOnly_ReleasesImmediatelyWithoutIncrement()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = (await SaveAndClaim(storeA, 1)).Single();

        // Act
        IReadOnlySet<long> retained = await storeA.ReleaseLockAsync([], [id]);

        // Assert
        retained.Should().BeEmpty();
        OutboxMessage row = await Row(id);
        row.LockedAt.Should().BeNull();
        row.LockedBy.Should().BeNull();
        row.RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task ReleaseLockAsync_RowsClaimedByAnotherInstance_ReleasesOnlyOwnClaims()
    {
        // Arrange — row X is claimed by instance "b", row Y by instance "a".
        var clock = new FakeTimeProvider(T0);
        long x = (await SaveAndClaim(Store("b", clock), 1)).Single();
        EfCoreOutboxStore storeA = Store("a", clock);
        long y = (await SaveAndClaim(storeA, 1)).Single();

        // Act — "a" names X in both lists, as if it had wrongly believed it owned it.
        await storeA.ReleaseLockAsync([x, y], [x]);

        // Assert — X is untouched, Y is released as a nack.
        OutboxMessage rowX = await Row(x);
        rowX.LockedBy.Should().Be("b");
        rowX.LockedAt.Should().Be(T0);
        rowX.RetryCount.Should().Be(0);

        OutboxMessage rowY = await Row(y);
        rowY.LockedBy.Should().BeNull();
        rowY.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task ReleaseLockAsync_DeliveredRow_IsNotReleased()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = (await SaveAndClaim(storeA, 1)).Single();
        await storeA.MarkDeliveredAsync([id]);

        // Act
        await storeA.ReleaseLockAsync([id], [id]);

        // Assert
        OutboxMessage row = await Row(id);
        row.LockedBy.Should().Be("a");
        row.LockedAt.Should().Be(T0);
        row.RetryCount.Should().Be(0);
        row.DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ReleaseLockAsync_IdInBothLists_IsTreatedAsNack()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = (await SaveAndClaim(storeA, 1)).Single();

        // Act
        await storeA.ReleaseLockAsync([id], [id]);

        // Assert
        OutboxMessage row = await Row(id);
        row.LockedBy.Should().BeNull();
        row.RetryCount.Should().Be(1);
        row.LockedAt.Should().Be(T0 - _options.OutboxLockTimeout + ExpectedDeferral(0, id));
    }

    [Fact]
    public async Task ReleaseLockAsync_ShorthandOverload_TreatsIdsAsNacks()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = (await SaveAndClaim(storeA, 1)).Single();

        // Act
        IReadOnlySet<long> retained = await storeA.ReleaseLockAsync([id]);

        // Assert
        retained.Should().BeEmpty();
        OutboxMessage row = await Row(id);
        row.LockedBy.Should().BeNull();
        row.RetryCount.Should().Be(1);
        row.LockedAt.Should().Be(T0 - _options.OutboxLockTimeout + ExpectedDeferral(0, id));
    }

    [Fact]
    public async Task ReleaseLockAsync_BothListsEmpty_ReturnsEmptySetAndLeavesRowsUntouched()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = (await SaveAndClaim(storeA, 1)).Single();

        // Act
        IReadOnlySet<long> retained = await storeA.ReleaseLockAsync([], []);

        // Assert
        retained.Should().BeEmpty();
        OutboxMessage row = await Row(id);
        row.LockedBy.Should().Be("a");
        row.LockedAt.Should().Be(T0);
        row.RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task ReleaseLockAsync_NullList_ThrowsArgumentNullException()
    {
        // Arrange
        EfCoreOutboxStore store = Store("a", new FakeTimeProvider(T0));

        // Act
        Func<Task> nullNacked = async () => await store.ReleaseLockAsync(null!, []);
        Func<Task> nullBarrier = async () => await store.ReleaseLockAsync([], null!);

        // Assert
        await nullNacked.Should().ThrowAsync<ArgumentNullException>().WithParameterName("nackedIds");
        await nullBarrier.Should().ThrowAsync<ArgumentNullException>().WithParameterName("barrierReleasedIds");
    }

    [Fact]
    public async Task ReleaseLockAsync_NackedAndBarrierIds_ExecuteAsSingleUpdateStatement()
    {
        // Arrange — a single statement leaves no window in which the nacked head of a key is released
        // while its barrier-held siblings are still owned (and re-sent ahead of it) by this instance.
        var counter = new NonQueryCommandCounter();
        await using var context = new OutboxDbContext(ContextOptions().AddInterceptors(counter).Options);
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock, context);
        List<long> ids = await SaveAndClaim(storeA, 2, context);
        counter.Reset();

        // Act
        await storeA.ReleaseLockAsync([ids[0]], [ids[1]]);

        // Assert
        counter.CommandTexts.Should().ContainSingle()
            .Which.TrimStart().Should().StartWithEquivalentOf("UPDATE");
        (await Row(ids[0])).RetryCount.Should().Be(1);
        (await Row(ids[1])).RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task ReleaseLockAsync_RepeatedNackReleases_DoNotRecompileQuery()
    {
        // Arrange — a context with its own service provider, so its query cache starts empty.
        int compilations = 0;
        DbContextOptions<OutboxDbContext> options = ContextOptions()
            .EnableServiceProviderCaching(false)
            .LogTo(_ => compilations++, [CoreEventId.QueryCompilationStarting])
            .Options;
        await using var context = new OutboxDbContext(options);
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock, context);
        long id = (await SaveAndClaim(storeA, 1, context)).Single();

        int beforeFirst = compilations;
        await storeA.ReleaseLockAsync([id], []);
        int firstReleaseCompilations = compilations - beforeFirst;

        clock.Advance(ExpectedDeferral(0, id) + TimeSpan.FromSeconds(1));
        IReadOnlyList<OutboxEntry> reclaimed = await storeA.GetPendingAsync(10);
        ReturnBuffers(reclaimed);
        reclaimed.Should().ContainSingle();

        // Act — a later release at a different instant must reuse the cached translation.
        int beforeSecond = compilations;
        await storeA.ReleaseLockAsync([id], []);

        // Assert
        firstReleaseCompilations.Should().BePositive("the first release must be observed compiling");
        (compilations - beforeSecond).Should().Be(0, "deferral cells are query parameters, not constants");
    }

    private DbContextOptionsBuilder<OutboxDbContext> ContextOptions()
        => new DbContextOptionsBuilder<OutboxDbContext>().UseSqlite(_connection);

    private EfCoreOutboxStore Store(string instance, TimeProvider clock, OutboxDbContext? context = null)
        => new(
            context ?? _dbContext,
            new OutboxInstanceId(instance),
            new PostgresOutboxSqlDialect(),
            _options,
            clock,
            new FixedJitterSource(Jitter));

    // The deferral the store is expected to apply, computed from the same schedule and jitter.
    private TimeSpan ExpectedDeferral(int retryCount, long id)
        => OutboxNackDeferralSchedule.FromOptions(_options, new FixedJitterSource(Jitter))
            .CreatePlan()
            .GetDeferralForRow(retryCount, id);

    // Saves the given number of messages through the store, claims them, and returns their ids.
    private async Task<List<long>> SaveAndClaim(EfCoreOutboxStore store, int count, OutboxDbContext? context = null)
    {
        await store.SaveMessagesAsync([.. Enumerable.Range(0, count).Select(_ => Message())]);
        await (context ?? _dbContext).SaveChangesAsync();

        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);
        ReturnBuffers(batch);
        batch.Should().HaveCount(count);

        return [.. batch.Select(e => e.Id)];
    }

    private Task<int> SetRetryCount(long id, int retryCount)
        => _dbContext.Set<OutboxMessage>()
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.RetryCount, retryCount));

    // Fresh read that bypasses the change tracker (ExecuteUpdate does not refresh tracked entities).
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

    private sealed class NonQueryCommandCounter : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = [];

        public IReadOnlyList<string> CommandTexts => _commandTexts;

        public void Reset() => _commandTexts.Clear();

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _commandTexts.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
