using System.Buffers;
using AwesomeAssertions;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Outbox;

/// <summary>
/// Drives <see cref="OutboxSingleSlotTurn"/> directly and through <see cref="EfCoreOutboxStore"/> —
/// proves the alternation sequence, strict alternation across two store instances sharing one turn,
/// and that <c>AddBareWireOutbox</c> wires exactly one turn singleton per service provider.
/// </summary>
public sealed class OutboxSingleSlotTurnTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly OutboxOptions _options = new();
    private SqliteConnection _connection = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        await using OutboxDbContext schemaContext = CreateDbContext();
        await schemaContext.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public void NextIsRetryTurn_ConsecutiveCalls_AlternatesStartingWithNewRows()
    {
        var turn = new OutboxSingleSlotTurn();
        bool[] turns = [.. Enumerable.Range(0, 6).Select(_ => turn.NextIsRetryTurn())];
        turns.Should().Equal(false, true, false, true, false, true);
    }

    [Fact]
    public async Task GetPendingAsync_TwoStoreInstancesSharingTurn_AlternateClassesStrictly()
    {
        HashSet<long> newIds;
        await using (OutboxDbContext seedContext = CreateDbContext())
        {
            newIds = [.. await SeedAsync(seedContext, [.. Enumerable.Range(0, 6).Select(_ => NewRow())])];
            await SeedAsync(seedContext, [.. Enumerable.Range(0, 6).Select(i => DueRow(i + 1))]);
        }

        var clock = new FakeTimeProvider(T0);
        var shared = new OutboxSingleSlotTurn();

        // Constant jitter that would have handed EVERY contested slot to the retry class under the old
        // draw — the alternation must not depend on it once the shared turn decides.
        var jitter = new FixedJitterSource(0.1);

        EfCoreOutboxStore first = Store("a", clock, jitter, shared);
        EfCoreOutboxStore second = Store("a", clock, jitter, shared);

        var kinds = new List<string>();
        for (int cycle = 0; cycle < 6; cycle++)
        {
            EfCoreOutboxStore store = cycle % 2 == 0 ? first : second;
            IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(1);
            try
            {
                batch.Should().ContainSingle();
                long id = batch[0].Id;
                kinds.Add(newIds.Contains(id) ? "new" : "retry");
                await store.MarkDeliveredAsync([id]);
            }
            finally
            {
                ReturnBuffers(batch);
            }
        }

        kinds.Should().Equal("new", "retry", "new", "retry", "new", "retry");
    }

    [Fact]
    public async Task AddBareWireOutbox_StoresFromSeparateScopes_ShareOneSingleSlotTurn()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireOutbox(configureDbContext: o => o.UseSqlite("DataSource=:memory:"));

        await using ServiceProvider provider = services.BuildServiceProvider();
        OutboxSingleSlotTurn singleton = provider.GetRequiredService<OutboxSingleSlotTurn>();

        await using AsyncServiceScope s1 = provider.CreateAsyncScope();
        await using AsyncServiceScope s2 = provider.CreateAsyncScope();
        var a = (EfCoreOutboxStore)s1.ServiceProvider.GetRequiredService<IOutboxStore>();
        var b = (EfCoreOutboxStore)s2.ServiceProvider.GetRequiredService<IOutboxStore>();

        a.Should().NotBeSameAs(b);
        a.SingleSlotTurn.Should().BeSameAs(singleton);
        b.SingleSlotTurn.Should().BeSameAs(singleton);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private OutboxDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<OutboxDbContext>().UseSqlite(_connection).Options);

    private static OutboxMessage NewRow()
        => new()
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "test.routing",
            ContentType = "application/json",
            Payload = [1],
            CreatedAt = T0,
        };

    private static OutboxMessage DueRow(int overdueBySeconds, int retryCount = 1)
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
        };

    private static async Task<List<long>> SeedAsync(OutboxDbContext context, IReadOnlyList<OutboxMessage> rows)
    {
        context.OutboxMessages.AddRange(rows);
        await context.SaveChangesAsync();
        return [.. rows.Select(r => r.Id)];
    }

    private EfCoreOutboxStore Store(
        string instance,
        TimeProvider clock,
        IOutboxJitterSource jitter,
        OutboxSingleSlotTurn turn)
        => new(
            CreateDbContext(),
            new OutboxInstanceId(instance),
            new PostgresOutboxSqlDialect(),
            _options,
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
