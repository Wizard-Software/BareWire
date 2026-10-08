using System.Buffers;
using System.Data.Common;
using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Outbox;

/// <summary>
/// Exercises <see cref="TransactionalOutboxMiddleware"/> on a real SQLite in-memory database with
/// the EF Core ambient-transaction warning left at its default (error) severity.
/// </summary>
public sealed class TransactionalOutboxMiddlewareSqliteTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private OutboxDbContext _dbContext = null!;
    private EfCoreOutboxStore _outboxStore = null!;
    private EfCoreInboxStore _inboxStore = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        // Deliberately no ConfigureWarnings(...Ignore(AmbientTransactionWarning)).
        DbContextOptions<OutboxDbContext> options = new DbContextOptionsBuilder<OutboxDbContext>()
            .UseSqlite(_connection)
            .Options;
        _dbContext = new OutboxDbContext(options);
        await _dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        _outboxStore = new EfCoreOutboxStore(
            _dbContext,
            new OutboxInstanceId("test-instance"),
            new PostgresOutboxSqlDialect(),
            new OutboxOptions());
        _inboxStore = new EfCoreInboxStore(_dbContext, new PostgresInboxSqlDialect());
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private TransactionalOutboxMiddleware CreateMiddleware(IInboxStore inboxStore, OutboxDbContext? dbContext = null)
    {
        var inboxFilter = new InboxFilter(
            inboxStore,
            new OutboxOptions
            {
                InboxLockTimeout = TimeSpan.FromMinutes(5),
                InboxRetention = TimeSpan.FromDays(7)
            },
            NullLogger<InboxFilter>.Instance);

        return new TransactionalOutboxMiddleware(
            dbContext ?? _dbContext,
            _outboxStore,
            inboxFilter,
            NullLogger<TransactionalOutboxMiddleware>.Instance,
            new OutboxTransactionMode(useAmbientTransaction: false));
    }

    private static MessageContext CreateContext() =>
        new(
            Guid.NewGuid(),
            new Dictionary<string, string> { ["BW-MessageType"] = "OrderCreated" },
            ReadOnlySequence<byte>.Empty,
            Substitute.For<IServiceProvider>(),
            endpointName: "test-endpoint");

    private static NextMiddleware PublishOneMessage() => _ =>
    {
        TransactionalOutboxMiddleware.Current.Should().NotBeNull();
        TransactionalOutboxMiddleware.Current!.Add(new OutboundMessage(
            "payments.requested",
            new Dictionary<string, string>(),
            "{\"amount\":100}"u8.ToArray(),
            "application/json"));
        return Task.CompletedTask;
    };

    [Fact]
    public async Task InvokeAsync_OnSqliteWithoutWarningSuppression_CommitsOutboxMessageAndInboxMarker()
    {
        // Arrange
        TransactionalOutboxMiddleware middleware = CreateMiddleware(_inboxStore);

        // Act
        await middleware.InvokeAsync(CreateContext(), PublishOneMessage());

        // Assert
        _dbContext.ChangeTracker.Clear();
        (await _dbContext.Set<OutboxMessage>().CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(1);
        (await _dbContext.Set<InboxMessage>().SingleAsync(TestContext.Current.CancellationToken))
            .ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task InvokeAsync_OnSqliteWhenMarkProcessedFails_RollsBackOutboxMessage()
    {
        // Arrange
        TransactionalOutboxMiddleware middleware = CreateMiddleware(new ThrowingMarkProcessedInboxStore(_inboxStore));

        // Act
        Func<Task> act = () => middleware.InvokeAsync(CreateContext(), PublishOneMessage());

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated marker failure");
        _dbContext.ChangeTracker.Clear();
        (await _dbContext.Set<OutboxMessage>().CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(0, "the outbox rows must roll back together with the failed inbox marker");
    }

    [Fact]
    public async Task InvokeAsync_OnSqlite_ExposesLocalTransactionAndNoAmbientTransactionToHandler()
    {
        // Arrange
        TransactionalOutboxMiddleware middleware = CreateMiddleware(_inboxStore);
        IOutboxConnectionAccessor accessor = new OutboxConnectionAccessor();
        DbTransaction? seen = null;
        System.Transactions.Transaction? ambient = System.Transactions.Transaction.Current;
        NextMiddleware next = _ =>
        {
            seen = accessor.CurrentTransaction;
            ambient = System.Transactions.Transaction.Current;
            return Task.CompletedTask;
        };

        // Act
        await middleware.InvokeAsync(CreateContext(), next);

        // Assert
        seen.Should().NotBeNull("the local transaction must be exposed while the handler runs");
        ambient.Should().BeNull("local mode must not open an ambient TransactionScope");
        accessor.CurrentTransaction.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_WhenConsumerCommitsTheLocalTransaction_ThrowsInvalidOperationException()
    {
        // Arrange
        TransactionalOutboxMiddleware middleware = CreateMiddleware(_inboxStore);
        IOutboxConnectionAccessor accessor = new OutboxConnectionAccessor();
        NextMiddleware next = _ =>
        {
            accessor.CurrentTransaction!.Commit();
            return Task.CompletedTask;
        };

        // Act
        Func<Task> act = () => middleware.InvokeAsync(CreateContext(), next);

        // Assert
        InvalidOperationException ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("owned by the transactional outbox middleware");
        ex.Message.Should().NotContainAny("DataSource", "Data Source", "Password");
        _dbContext.ChangeTracker.Clear();
        (await _dbContext.Set<InboxMessage>().AnyAsync(m => m.ProcessedAt != null, TestContext.Current.CancellationToken))
            .Should().BeFalse("a consumer-completed transaction must not be treated as a successful consume");
    }

    [Fact]
    public async Task InvokeAsync_WhenHandlerThrowsAndTransactionDisposeThrows_PropagatesHandlerException()
    {
        // Arrange — a context whose transaction disposal fails.
        DbContextOptions<OutboxDbContext> options = new DbContextOptionsBuilder<OutboxDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new ThrowOnTransactionDisposeInterceptor())
            .Options;
        await using var dbContext = new OutboxDbContext(options);
        TransactionalOutboxMiddleware middleware = CreateMiddleware(_inboxStore, dbContext);
        NextMiddleware next = _ => throw new InvalidOperationException("handler fault");

        // Act
        Func<Task> act = () => middleware.InvokeAsync(CreateContext(), next);

        // Assert — the dispose failure must not replace the handler's exception.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("handler fault");
    }

    [Fact]
    public async Task InvokeAsync_WhenTaskStartedInHandlerOutlivesIt_SeesTransactionAlreadyEnded()
    {
        // Arrange
        TransactionalOutboxMiddleware middleware = CreateMiddleware(_inboxStore);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DbTransaction?>? leaked = null;
        NextMiddleware next = _ =>
        {
            // Captures the execution context while the transaction is still published on it.
            leaked = Task.Run(async () =>
            {
                await gate.Task;
                return TransactionalOutboxMiddleware.CurrentTransaction;
            });
            return Task.CompletedTask;
        };

        // Act
        await middleware.InvokeAsync(CreateContext(), next);
        gate.SetResult();
        DbTransaction? seen = await leaked!;

        // Assert — either nothing is exposed any more, or the transaction it exposes has ended.
        (seen is null || seen.Connection is null).Should().BeTrue(
            "work that outlives the handler must never be able to use a still-open transaction");
        TransactionalOutboxMiddleware.CurrentTransaction.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_WhenConsumerDbContextJoinsCurrentTransaction_BusinessWriteIsAtomicWithOutbox(
        bool markProcessedFails)
    {
        // Arrange
        await using (DbCommand create = _connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE Rows (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL)";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        IOutboxConnectionAccessor accessor = new OutboxConnectionAccessor();
        IInboxStore inboxStore = markProcessedFails ? new ThrowingMarkProcessedInboxStore(_inboxStore) : _inboxStore;
        TransactionalOutboxMiddleware middleware = CreateMiddleware(inboxStore);
        NextMiddleware next = async _ =>
        {
            DbTransaction transaction = accessor.CurrentTransaction!;
            DbContextOptions<BusinessDbContext> businessOptions = new DbContextOptionsBuilder<BusinessDbContext>()
                .UseSqlite(accessor.Current!)
                .Options;
            await using var business = new BusinessDbContext(businessOptions);
            await business.Database.UseTransactionAsync(transaction);
            business.Rows.Add(new BusinessRow { Name = "order-1" });
            await business.SaveChangesAsync();
        };

        // Act
        Func<Task> act = () => middleware.InvokeAsync(CreateContext(), next);
        if (markProcessedFails)
        {
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated marker failure");
        }
        else
        {
            await act();
        }

        // Assert
        await using DbCommand count = _connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Rows";
        long rows = (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        rows.Should().Be(markProcessedFails ? 0 : 1);
        accessor.CurrentTransaction.Should().BeNull();
    }

    private sealed class BusinessRow
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class BusinessDbContext(DbContextOptions<BusinessDbContext> options) : DbContext(options)
    {
        public DbSet<BusinessRow> Rows => Set<BusinessRow>();
    }

    // Replaces the transaction EF Core begins with a wrapper whose disposal fails after the inner
    // transaction was released — simulates a rollback/dispose failure during cleanup.
    private sealed class ThrowOnTransactionDisposeInterceptor : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            DbTransaction inner = await connection
                .BeginTransactionAsync(eventData.IsolationLevel, cancellationToken)
                .ConfigureAwait(false);
            return InterceptionResult<DbTransaction>.SuppressWithResult(new ThrowOnDisposeTransaction(inner));
        }
    }

    // The base Dispose is intentionally skipped: the wrapper only delegates to the inner transaction.
#pragma warning disable CA2215
    private sealed class ThrowOnDisposeTransaction(DbTransaction inner) : DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => inner.IsolationLevel;

        protected override DbConnection? DbConnection => inner.Connection;

        public override void Commit() => inner.Commit();

        public override void Rollback() => inner.Rollback();

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("simulated dispose failure");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                throw new InvalidOperationException("simulated dispose failure");
            }
        }
    }
#pragma warning restore CA2215

    private sealed class ThrowingMarkProcessedInboxStore(IInboxStore inner) : IInboxStore
    {
        public ValueTask<bool> TryLockAsync(
            Guid messageId, string consumerType, TimeSpan lockTimeout, CancellationToken cancellationToken = default)
            => inner.TryLockAsync(messageId, consumerType, lockTimeout, cancellationToken);

        public ValueTask MarkProcessedAsync(
            Guid messageId, string consumerType, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated marker failure");

        public ValueTask CleanupAsync(TimeSpan retention, CancellationToken cancellationToken = default)
            => inner.CleanupAsync(retention, cancellationToken);
    }
}
