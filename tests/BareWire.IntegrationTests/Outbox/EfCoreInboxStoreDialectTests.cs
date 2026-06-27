using AwesomeAssertions;
using BareWire.Outbox.EntityFramework;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BareWire.IntegrationTests.Outbox;

/// <summary>
/// Verifies that <see cref="EfCoreInboxStore"/> selects the atomic re-lock path in a
/// <b>dialect-driven</b> way — gated on <c>IInboxSqlDialect.ProviderName</c> matching the active
/// EF Core provider — rather than hard-coding PostgreSQL. A provider other than PostgreSQL that
/// supplies a matching dialect must get the atomic re-lock, not the non-atomic fallback.
/// </summary>
public sealed class EfCoreInboxStoreDialectTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private OutboxDbContext _dbContext = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<OutboxDbContext>()
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
    public async Task TryLockAsync_WhenDialectProviderMatchesActiveProvider_UsesDialectAtomicReLock()
    {
        // Active provider is SQLite; the custom dialect declares ProviderName = SQLite, so the store
        // must route the re-lock through the dialect's atomic SQL. If selection were hard-wired to
        // Npgsql (the regression), GetReLockSql would never be called for a SQLite deployment.
        var spy = new SpyInboxSqlDialect();
        var store = new EfCoreInboxStore(_dbContext, spy);

        Guid messageId = Guid.NewGuid();
        const string consumerType = "Consumer";

        // Fresh insert — a re-lock must NOT be attempted yet.
        bool locked = await store.TryLockAsync(
            messageId, consumerType, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        locked.Should().BeTrue();
        spy.ReLockSqlCallCount.Should().Be(0, "the first lock is a fresh insert, not a re-lock");

        await Task.Delay(20); // let the lock expire

        bool relocked = await store.TryLockAsync(
            messageId, consumerType, TimeSpan.FromMinutes(5), CancellationToken.None);

        relocked.Should().BeTrue("the expired, unprocessed row must be re-acquirable via the dialect SQL");
        spy.ReLockSqlCallCount.Should().BeGreaterThan(0,
            "a dialect whose ProviderName matches the active provider must be used for the atomic " +
            "re-lock — selection must be dialect-driven, not hard-wired to Npgsql");
    }

    // A dialect that targets the SQLite provider and records whether its atomic re-lock SQL was used.
    private sealed class SpyInboxSqlDialect : IInboxSqlDialect
    {
        public int ReLockSqlCallCount;

        public string ProviderName => "Microsoft.EntityFrameworkCore.Sqlite";

        public FormattableString GetUpsertSql(
            Guid messageId, string consumerType, DateTimeOffset receivedAt, DateTimeOffset expiresAt)
            => $"""
                INSERT INTO "InboxMessages" ("MessageId", "ConsumerType", "ReceivedAt", "ExpiresAt")
                VALUES ({messageId}, {consumerType}, {receivedAt}, {expiresAt})
                ON CONFLICT ("MessageId", "ConsumerType") DO NOTHING
                """;

        public FormattableString GetReLockSql(
            Guid messageId, string consumerType, DateTimeOffset now, DateTimeOffset newExpiresAt)
        {
            Interlocked.Increment(ref ReLockSqlCallCount);
            return $"""
                UPDATE "InboxMessages"
                SET "ExpiresAt" = {newExpiresAt}
                WHERE "MessageId" = {messageId}
                  AND "ConsumerType" = {consumerType}
                  AND "ExpiresAt" < {now}
                  AND "ProcessedAt" IS NULL
                """;
        }
    }
}
