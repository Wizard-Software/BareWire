using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Outbox;

// Drives EfCoreOutboxStore time through an injected FakeTimeProvider. SQLite does not match the
// PostgreSQL dialect's provider name, so the store takes the client-side fallback claim path, which
// compares LockedAt with the stale-lock cutoff in memory.
public sealed class EfCoreOutboxStoreClockTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly OutboxOptions _options = new();
    private SqliteConnection _connection = null!;
    private OutboxDbContext _dbContext = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        DbContextOptions<OutboxDbContext> options = new DbContextOptionsBuilder<OutboxDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new OutboxDbContext(options);
        await _dbContext.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SaveMessagesAsync_WithFakeTimeProvider_StampsCreatedAtFromInjectedClock()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);

        // Act
        await Store("a", clock).SaveMessagesAsync([Message()]);
        await _dbContext.SaveChangesAsync();

        // Assert
        (await Row()).CreatedAt.Should().Be(T0);
    }

    [Fact]
    public async Task GetPendingAsync_WithFakeTimeProvider_StampsLockedAtWithClaimTime()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore store = Store("a", clock);
        await store.SaveMessagesAsync([Message()]);
        await _dbContext.SaveChangesAsync();
        clock.Advance(TimeSpan.FromSeconds(7));

        // Act
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        // Assert
        ReturnBuffers(batch);
        batch.Should().ContainSingle();
        OutboxMessage row = await Row();
        row.LockedAt.Should().Be(T0 + TimeSpan.FromSeconds(7));
        row.LockedBy.Should().Be("a");
    }

    [Fact]
    public async Task GetPendingAsync_WhenClaimYoungerThanLockTimeout_OtherInstanceCannotClaim()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        await ClaimByA(clock);
        clock.Advance(_options.OutboxLockTimeout - TimeSpan.FromSeconds(1));

        // Act
        IReadOnlyList<OutboxEntry> batch = await Store("b", clock).GetPendingAsync(10);

        // Assert
        batch.Should().BeEmpty();
        (await Row()).LockedBy.Should().Be("a");
    }

    [Fact]
    public async Task GetPendingAsync_WhenClaimOlderThanLockTimeout_OtherInstanceReclaimsAtInjectedTime()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        await ClaimByA(clock);
        clock.Advance(_options.OutboxLockTimeout + TimeSpan.FromSeconds(1));

        // Act
        IReadOnlyList<OutboxEntry> batch = await Store("b", clock).GetPendingAsync(10);

        // Assert
        ReturnBuffers(batch);
        batch.Should().ContainSingle();
        OutboxMessage row = await Row();
        row.LockedBy.Should().Be("b");
        row.LockedAt.Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task ReleaseLockAsync_ThenOtherInstanceClaims_StampsLockedAtWithInjectedTime()
    {
        // Arrange — instance "a" claims at T0 and releases; the clock then moves by less than the
        // lock timeout, so without the release instance "b" could not take the row yet.
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore storeA = Store("a", clock);
        long id = await ClaimByA(clock, storeA);
        await storeA.ReleaseLockAsync([id]);
        clock.Advance(TimeSpan.FromSeconds(3));

        // Act
        IReadOnlyList<OutboxEntry> batch = await Store("b", clock).GetPendingAsync(10);

        // Assert
        ReturnBuffers(batch);
        batch.Should().ContainSingle();
        OutboxMessage row = await Row();
        row.LockedBy.Should().Be("b");
        row.LockedAt.Should().Be(T0 + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task MarkDeliveredAsync_WhenFakeClockAdvanced_StampsDeliveredAtWithAdvancedTime()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        EfCoreOutboxStore store = Store("a", clock);
        long id = await ClaimByA(clock, store);
        clock.Advance(TimeSpan.FromMinutes(2));

        // Act
        await store.MarkDeliveredAsync([id]);

        // Assert
        (await Row()).DeliveredAt.Should().Be(T0 + TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Constructor_WithoutClockOrJitter_DefaultsToSystemClockAndSharedRandom()
    {
        // Act
        var store = new EfCoreOutboxStore(
            _dbContext,
            new OutboxInstanceId("a"),
            new PostgresOutboxSqlDialect(),
            _options);

        // Assert
        store.TimeProvider.Should().BeSameAs(TimeProvider.System);
        store.JitterSource.Should().BeSameAs(SharedRandomOutboxJitterSource.Instance);
    }

    private EfCoreOutboxStore Store(string instance, TimeProvider clock)
        => new(_dbContext, new OutboxInstanceId(instance), new PostgresOutboxSqlDialect(), _options, clock);

    // Saves one message through instance "a", claims it, and returns the claimed row id.
    private async Task<long> ClaimByA(FakeTimeProvider clock, EfCoreOutboxStore? store = null)
    {
        store ??= Store("a", clock);
        await store.SaveMessagesAsync([Message()]);
        await _dbContext.SaveChangesAsync();

        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);
        ReturnBuffers(batch);

        return batch.Should().ContainSingle().Which.Id;
    }

    // Fresh read that bypasses the change tracker (ExecuteUpdate does not refresh tracked entities).
    private Task<OutboxMessage> Row()
        => _dbContext.Set<OutboxMessage>().AsNoTracking().SingleAsync();

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
}
