using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Pipeline;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Outbox;

/// <summary>
/// Guards the deferred-BEGIN behaviour of the local consume transaction on a file-based SQLite
/// database in WAL mode: while a handler runs, other connections must still be able to write.
/// </summary>
public sealed class TransactionalOutboxMiddlewareWalTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"barewire-wal-{Guid.NewGuid():N}.db");

    public async ValueTask InitializeAsync()
    {
        await using var setup = new SqliteConnection($"Data Source={_path};Pooling=False");
        await setup.OpenAsync(TestContext.Current.CancellationToken);
        await using SqliteCommand wal = setup.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        await wal.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in (string[])["", "-wal", "-shm"])
        {
            File.Delete(_path + suffix);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task InvokeAsync_WhileHandlerWaits_DoesNotHoldTheDatabaseWriteLock()
    {
        // Arrange
        DbContextOptions<OutboxDbContext> options = new DbContextOptionsBuilder<OutboxDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False")
            .Options;
        await using var dbContext = new OutboxDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Probe (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL)",
            TestContext.Current.CancellationToken);

        var inboxStore = new EfCoreInboxStore(dbContext, new PostgresInboxSqlDialect());
        var inboxFilter = new InboxFilter(
            inboxStore,
            new OutboxOptions { InboxLockTimeout = TimeSpan.FromMinutes(5), InboxRetention = TimeSpan.FromDays(7) },
            NullLogger<InboxFilter>.Instance);
        var middleware = new TransactionalOutboxMiddleware(
            dbContext,
            Substitute.For<IOutboxStore>(),
            inboxFilter,
            NullLogger<TransactionalOutboxMiddleware>.Instance,
            new OutboxTransactionMode(useAmbientTransaction: false));

        var insideHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NextMiddleware next = async _ =>
        {
            insideHandler.SetResult();
            await release.Task;
        };
        var context = new MessageContext(
            Guid.NewGuid(),
            new Dictionary<string, string>(),
            ReadOnlySequence<byte>.Empty,
            Substitute.For<IServiceProvider>(),
            endpointName: "wal-endpoint");

        Task consume = middleware.InvokeAsync(context, next);
        await insideHandler.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        try
        {
            // Act — a second connection with a short busy timeout writes while the handler is parked.
            await using var other = new SqliteConnection($"Data Source={_path};Pooling=False;Default Timeout=1");
            await other.OpenAsync(TestContext.Current.CancellationToken);
            await using SqliteCommand insert = other.CreateCommand();
            insert.CommandText = "INSERT INTO Probe (Name) VALUES ('concurrent')";
            Func<Task<int>> act = () => insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            // Assert
            (await act.Should().NotThrowAsync()).Subject.Should().Be(1);
        }
        finally
        {
            release.SetResult();
            await consume;
        }
    }
}
