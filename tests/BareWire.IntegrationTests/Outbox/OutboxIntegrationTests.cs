using System.Buffers;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.IntegrationTests.Outbox;

// ---------------------------------------------------------------------------
// EfCoreOutboxStore integration tests
// ---------------------------------------------------------------------------

/// <summary>
/// Integration tests for <see cref="EfCoreOutboxStore"/> against SQLite in-memory.
/// Each test creates an isolated DbContext + schema so tests are independent.
/// </summary>
/// <remarks>
/// Cleanup tests (scenarios 3) are skipped on SQLite because
/// <c>ExecuteDeleteAsync</c> with subquery patterns is not supported by the
/// SQLite EF Core provider. They are designed to run against SQL Server / PostgreSQL.
/// </remarks>
public sealed class EfCoreOutboxStoreTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private OutboxDbContext _dbContext = null!;
    private EfCoreOutboxStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<OutboxDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new OutboxDbContext(options);
        await _dbContext.Database.EnsureCreatedAsync();

        _store = new EfCoreOutboxStore(
            _dbContext,
            new OutboxInstanceId("test-instance"),
            new PostgresOutboxSqlDialect(),
            new OutboxOptions());
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task EfCoreOutboxStore_SaveAndGetPending_ReturnsSavedMessages()
    {
        // Arrange
        byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"order\":1}");
        var messages = new List<OutboundMessage>
        {
            new("orders.created", new Dictionary<string, string> { ["x-type"] = "OrderCreated" }, bodyBytes, "application/json"),
            new("orders.shipped", new Dictionary<string, string>(), bodyBytes, "application/json")
        };

        // Act — SaveMessagesAsync does NOT call SaveChanges; the test simulates the middleware commit.
        await _store.SaveMessagesAsync(messages, CancellationToken.None);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        IReadOnlyList<OutboxEntry> pending = await _store.GetPendingAsync(100, CancellationToken.None);

        // Assert
        pending.Should().HaveCount(2);
        pending.Should().Contain(e => e.RoutingKey == "orders.created");
        pending.Should().Contain(e => e.RoutingKey == "orders.shipped");

        OutboxEntry withHeaders = pending.Single(e => e.RoutingKey == "orders.created");
        withHeaders.Headers.Should().ContainKey("x-type").WhoseValue.Should().Be("OrderCreated");

        string roundTripped = Encoding.UTF8.GetString(withHeaders.PooledBody, 0, withHeaders.BodyLength);
        roundTripped.Should().Be("{\"order\":1}");

        ReturnPooledBuffers(pending);
    }

    [Fact]
    public async Task EfCoreOutboxStore_MarkDelivered_RemovesFromPending()
    {
        // Arrange
        byte[] body = Encoding.UTF8.GetBytes("{}");
        var messages = new List<OutboundMessage>
        {
            new("cmd.pay", new Dictionary<string, string>(), body, "application/json"),
            new("cmd.ship", new Dictionary<string, string>(), body, "application/json")
        };

        await _store.SaveMessagesAsync(messages, CancellationToken.None);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        IReadOnlyList<OutboxEntry> pending = await _store.GetPendingAsync(100, CancellationToken.None);
        pending.Should().HaveCount(2);

        long[] idsToDeliver = [pending[0].Id];
        string remainingRoutingKey = pending[1].RoutingKey;
        ReturnPooledBuffers(pending);

        // Act
        await _store.MarkDeliveredAsync(idsToDeliver, CancellationToken.None);

        IReadOnlyList<OutboxEntry> remaining = await _store.GetPendingAsync(100, CancellationToken.None);

        // Assert — only the second message should remain pending
        remaining.Should().HaveCount(1);
        remaining[0].RoutingKey.Should().Be(remainingRoutingKey);

        ReturnPooledBuffers(remaining);
    }

    // -----------------------------------------------------------------------
    // Bug-reproduction tests (Krok 1 of 10.1 plan)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reproduces the bug: after <c>MarkDeliveredAsync</c> the next poll must not
    /// return the same messages.
    /// </summary>
    /// <remarks>
    /// This test is expected to be RED initially — it reproduces the bug where
    /// <c>ExecuteUpdateAsync</c> with <c>IReadOnlyList&lt;long&gt;.Contains()</c>
    /// produces a no-op SQL or is otherwise not applied, causing the dispatcher
    /// to re-publish the same outbox records indefinitely.
    /// </remarks>
    [Fact]
    public async Task MarkDeliveredAsync_AfterGetPending_MessagesNotReturnedOnNextPoll()
    {
        // Arrange — add 3 OutboxMessage entities directly via DbContext
        DateTimeOffset now = DateTimeOffset.UtcNow;

        _dbContext.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "orders.created",
            ContentType = "application/json",
            Payload = [1, 2, 3],
            CreatedAt = now
        });
        _dbContext.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "orders.updated",
            ContentType = "application/json",
            Payload = [4, 5, 6],
            CreatedAt = now
        });
        _dbContext.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "orders.completed",
            ContentType = "application/json",
            Payload = [7, 8, 9],
            CreatedAt = now
        });

        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // Act — first poll: should return all 3
        IReadOnlyList<OutboxEntry> firstPoll = await _store.GetPendingAsync(10, CancellationToken.None);
        firstPoll.Should().HaveCount(3);

        long[] ids = [firstPoll[0].Id, firstPoll[1].Id, firstPoll[2].Id];
        ReturnPooledBuffers(firstPoll);

        // Act — mark all as delivered
        await _store.MarkDeliveredAsync(ids, CancellationToken.None);

        // Act — second poll: should return 0 (bug: returns 3 again)
        IReadOnlyList<OutboxEntry> secondPoll = await _store.GetPendingAsync(10, CancellationToken.None);

        // Assert
        secondPoll.Should().BeEmpty("all messages were marked as delivered and must not appear in the next poll");

        ReturnPooledBuffers(secondPoll);
    }

    [Fact]
    public async Task GetPendingAsync_OnlyReturnsPending_SkipsDelivered()
    {
        // Arrange — add 3 OutboxMessage entities; pre-deliver one of them
        DateTimeOffset now = DateTimeOffset.UtcNow;

        _dbContext.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "orders.a",
            ContentType = "application/json",
            Payload = [1],
            CreatedAt = now
        });
        _dbContext.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "orders.b",
            ContentType = "application/json",
            Payload = [2],
            CreatedAt = now,
            DeliveredAt = now  // already delivered
        });
        _dbContext.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "orders.c",
            ContentType = "application/json",
            Payload = [3],
            CreatedAt = now
        });

        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // Act
        IReadOnlyList<OutboxEntry> pending = await _store.GetPendingAsync(10, CancellationToken.None);

        // Assert — only the 2 undelivered messages should be returned
        pending.Should().HaveCount(2);
        pending.Should().NotContain(e => e.RoutingKey == "orders.b", "delivered messages must be excluded from the poll");

        ReturnPooledBuffers(pending);
    }

    [Fact]
    public async Task MarkDeliveredAsync_PartialIds_OnlyMarksSpecified()
    {
        // Arrange — add 3 OutboxMessage entities
        DateTimeOffset now = DateTimeOffset.UtcNow;

        _dbContext.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "cmd.a",
            ContentType = "application/json",
            Payload = [1],
            CreatedAt = now
        });
        _dbContext.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "cmd.b",
            ContentType = "application/json",
            Payload = [2],
            CreatedAt = now
        });
        _dbContext.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            DestinationAddress = "cmd.c",
            ContentType = "application/json",
            Payload = [3],
            CreatedAt = now
        });

        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // Identify IDs — load them in order so we can mark only the first and third
        IReadOnlyList<OutboxEntry> allPending = await _store.GetPendingAsync(10, CancellationToken.None);
        allPending.Should().HaveCount(3);

        long id1 = allPending[0].Id;
        string remainingRoutingKey = allPending[1].RoutingKey;
        long id3 = allPending[2].Id;
        ReturnPooledBuffers(allPending);

        // Act — mark only id1 and id3 as delivered
        await _store.MarkDeliveredAsync([id1, id3], CancellationToken.None);

        // Assert — only the middle message (id2) should remain pending
        IReadOnlyList<OutboxEntry> remaining = await _store.GetPendingAsync(10, CancellationToken.None);

        remaining.Should().HaveCount(1);
        remaining[0].RoutingKey.Should().Be(remainingRoutingKey, "only the undelivered message must remain in the pending queue");

        ReturnPooledBuffers(remaining);
    }

    [Fact(Skip = "ExecuteDeleteAsync with subquery patterns is not supported by the SQLite EF Core provider. Run against SQL Server or PostgreSQL.")]
    public async Task EfCoreOutboxStore_Cleanup_RemovesDeliveredOlderThanRetention()
    {
        // Arrange
        byte[] body = Encoding.UTF8.GetBytes("{}");
        var messages = new List<OutboundMessage>
        {
            new("evt.old", new Dictionary<string, string>(), body, "application/json")
        };

        await _store.SaveMessagesAsync(messages, CancellationToken.None);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        IReadOnlyList<OutboxEntry> pending = await _store.GetPendingAsync(100, CancellationToken.None);
        long id = pending[0].Id;
        ReturnPooledBuffers(pending);

        // Mark delivered and backdate DeliveredAt to 30 days ago
        await _store.MarkDeliveredAsync([id], CancellationToken.None);

        DateTimeOffset ancientTime = DateTimeOffset.UtcNow - TimeSpan.FromDays(30);
        await _dbContext.Database.ExecuteSqlAsync(
            $"UPDATE OutboxMessages SET DeliveredAt = {ancientTime:O} WHERE Id = {id}");

        // Act — retention of 7 days; record is 30 days old
        await _store.CleanupAsync(TimeSpan.FromDays(7), CancellationToken.None);

        // Assert — record is physically gone
        int count = await _dbContext.OutboxMessages.CountAsync();
        count.Should().Be(0);
    }

    private static void ReturnPooledBuffers(IReadOnlyList<OutboxEntry> entries)
    {
        foreach (OutboxEntry entry in entries)
        {
            ArrayPool<byte>.Shared.Return(entry.PooledBody);
        }
    }
}

// ---------------------------------------------------------------------------
// EfCoreInboxStore integration tests
// ---------------------------------------------------------------------------

public sealed class EfCoreInboxStoreTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private OutboxDbContext _dbContext = null!;
    private EfCoreInboxStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<OutboxDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new OutboxDbContext(options);
        await _dbContext.Database.EnsureCreatedAsync();

        _store = new EfCoreInboxStore(_dbContext, new PostgresInboxSqlDialect());
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task EfCoreInboxStore_TryLock_FirstCallReturnsTrue()
    {
        // Arrange
        Guid messageId = Guid.NewGuid();

        // Act
        bool result = await _store.TryLockAsync(
            messageId,
            "OrderConsumer",
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task EfCoreInboxStore_TryLock_DuplicateReturnsFalse()
    {
        // Arrange
        Guid messageId = Guid.NewGuid();
        const string consumerType = "OrderConsumer";

        // First lock — must succeed
        bool first = await _store.TryLockAsync(messageId, consumerType, TimeSpan.FromMinutes(5), CancellationToken.None);
        first.Should().BeTrue();

        // Recreate the store with a fresh DbContext on the same connection to avoid
        // change-tracker state from the first SaveChangesAsync interfering.
        await _dbContext.DisposeAsync();
        var options = new DbContextOptionsBuilder<OutboxDbContext>()
            .UseSqlite(_connection)
            .Options;
        _dbContext = new OutboxDbContext(options);
        _store = new EfCoreInboxStore(_dbContext, new PostgresInboxSqlDialect());

        // Act — duplicate attempt with the same (MessageId, ConsumerType) key
        bool second = await _store.TryLockAsync(messageId, consumerType, TimeSpan.FromMinutes(5), CancellationToken.None);

        // Assert
        second.Should().BeFalse();
    }

    [Fact]
    public async Task EfCoreInboxStore_TryLock_ExpiredLockCanBeReacquired()
    {
        // Arrange
        Guid messageId = Guid.NewGuid();
        const string consumerType = "PaymentConsumer";

        // First lock — succeeds
        bool first = await _store.TryLockAsync(messageId, consumerType, TimeSpan.FromMinutes(5), CancellationToken.None);
        first.Should().BeTrue();

        // Manually expire the lock by backdating ExpiresAt via raw SQL
        DateTimeOffset expiredTime = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);
        await _dbContext.Database.ExecuteSqlAsync(
            $"UPDATE InboxMessages SET ExpiresAt = {expiredTime:O} WHERE MessageId = {messageId} AND ConsumerType = {consumerType}");

        _dbContext.ChangeTracker.Clear();

        // Act — should reacquire the expired lock
        bool reacquired = await _store.TryLockAsync(messageId, consumerType, TimeSpan.FromMinutes(5), CancellationToken.None);

        // Assert
        reacquired.Should().BeTrue();
    }

    [Fact]
    public async Task EfCoreInboxStore_ProcessedEntry_CannotBeRelocked()
    {
        // Arrange
        Guid messageId = Guid.NewGuid();
        const string consumerType = "AuditConsumer";

        // First lock — succeeds
        bool first = await _store.TryLockAsync(messageId, consumerType, TimeSpan.FromMinutes(5), CancellationToken.None);
        first.Should().BeTrue();

        // Backdate ExpiresAt AND set ProcessedAt via raw SQL — simulates a successfully processed message
        // whose lock has since expired. The inbox must still deny re-lock because ProcessedAt is set.
        DateTimeOffset expiredTime = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);
        DateTimeOffset processedTime = DateTimeOffset.UtcNow;
        await _dbContext.Database.ExecuteSqlAsync(
            $"UPDATE InboxMessages SET ExpiresAt = {expiredTime:O}, ProcessedAt = {processedTime:O} WHERE MessageId = {messageId} AND ConsumerType = {consumerType}");

        _dbContext.ChangeTracker.Clear();

        // Act — attempt to re-lock an expired but already-processed entry
        bool relocked = await _store.TryLockAsync(messageId, consumerType, TimeSpan.FromMinutes(5), CancellationToken.None);

        // Assert — must be denied: processed entries are permanently locked
        relocked.Should().BeFalse("a processed inbox entry must never be re-locked even after its lock expires");
    }

    [Fact(Skip = "ExecuteDeleteAsync with subquery patterns is not supported by the SQLite EF Core provider. Run against SQL Server or PostgreSQL.")]
    public async Task EfCoreInboxStore_Cleanup_RemovesExpiredEntries()
    {
        // Arrange — insert a lock and then expire it manually
        Guid messageId = Guid.NewGuid();
        const string consumerType = "CleanupConsumer";

        bool locked = await _store.TryLockAsync(messageId, consumerType, TimeSpan.FromMinutes(5), CancellationToken.None);
        locked.Should().BeTrue();

        DateTimeOffset ancientExpiry = DateTimeOffset.UtcNow - TimeSpan.FromDays(10);
        await _dbContext.Database.ExecuteSqlAsync(
            $"UPDATE InboxMessages SET ExpiresAt = {ancientExpiry:O} WHERE MessageId = {messageId}");

        _dbContext.ChangeTracker.Clear();

        // Act — retention of 7 days; entry is 10 days past expiry
        await _store.CleanupAsync(TimeSpan.FromDays(7), CancellationToken.None);

        // Assert
        int count = await _dbContext.InboxMessages.CountAsync();
        count.Should().Be(0);
    }
}

// ---------------------------------------------------------------------------
// TransactionalOutboxMiddleware integration tests
// ---------------------------------------------------------------------------

/// <summary>
/// Integration tests for <see cref="TransactionalOutboxMiddleware"/> against SQLite in-memory.
/// </summary>
/// <remarks>
/// SQLite does not support <c>System.Transactions.TransactionScope</c>. The middleware
/// creates a <c>TransactionScope</c> that triggers a warning from the EF Core SQLite
/// provider. The DbContext is configured with
/// <c>ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))</c>
/// so that the tests can exercise the middleware logic without transaction rollback
/// semantics. Atomicity tests that rely on rollback are skipped on SQLite.
/// </remarks>
public sealed class TransactionalOutboxMiddlewareTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private OutboxDbContext _dbContext = null!;
    private EfCoreOutboxStore _outboxStore = null!;
    private EfCoreInboxStore _inboxStore = null!;
    private InboxFilter _inboxFilter = null!;
    private TransactionalOutboxMiddleware _middleware = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _dbContext = CreateDbContext();
        await _dbContext.Database.EnsureCreatedAsync();

        RebuildComponents();
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// Creates a new <see cref="OutboxDbContext"/> sharing the open SQLite connection.
    /// The <see cref="RelationalEventId.AmbientTransactionWarning"/> is suppressed because
    /// SQLite cannot participate in <see cref="System.Transactions.TransactionScope"/>.
    /// </summary>
    private OutboxDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OutboxDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
            .Options;

        return new OutboxDbContext(options);
    }

    private void RebuildComponents()
    {
        _outboxStore = new EfCoreOutboxStore(
            _dbContext,
            new OutboxInstanceId("test-instance"),
            new PostgresOutboxSqlDialect(),
            new OutboxOptions());
        _inboxStore = new EfCoreInboxStore(_dbContext, new PostgresInboxSqlDialect());

        var outboxOptions = new OutboxOptions
        {
            InboxLockTimeout = TimeSpan.FromMinutes(5),
            InboxRetention = TimeSpan.FromDays(7)
        };

        _inboxFilter = new InboxFilter(
            _inboxStore,
            outboxOptions,
            NullLogger<InboxFilter>.Instance);

        _middleware = new TransactionalOutboxMiddleware(
            _dbContext,
            _outboxStore,
            _inboxFilter,
            NullLogger<TransactionalOutboxMiddleware>.Instance);
    }

    [Fact]
    public async Task TransactionalOutbox_HappyPath_BusinessStateAndOutboxSavedAtomically()
    {
        // Arrange
        Guid messageId = Guid.NewGuid();
        IServiceProvider sp = Substitute.For<IServiceProvider>();
        var headers = new Dictionary<string, string> { ["BW-MessageType"] = "OrderCreated" };
        var context = new MessageContext(messageId, headers, ReadOnlySequence<byte>.Empty, sp);

        byte[] body = Encoding.UTF8.GetBytes("{\"amount\":100}");
        var outboundMessage = new OutboundMessage(
            "payments.requested",
            new Dictionary<string, string>(),
            body,
            "application/json");

        // Handler adds a message to the outbox buffer exposed via the AsyncLocal
        NextMiddleware handler = ctx =>
        {
            OutboxBuffer? buffer = TransactionalOutboxMiddleware.Current;
            buffer.Should().NotBeNull("middleware must set the current buffer before invoking the handler");
            buffer!.Add(outboundMessage);
            return Task.CompletedTask;
        };

        // Act
        await _middleware.InvokeAsync(context, handler);

        // Assert — the middleware flushed the buffer and called SaveChangesAsync
        _dbContext.ChangeTracker.Clear();
        IReadOnlyList<OutboxEntry> pending = await _outboxStore.GetPendingAsync(100, CancellationToken.None);

        pending.Should().HaveCount(1);
        pending[0].RoutingKey.Should().Be("payments.requested");
        pending[0].ContentType.Should().Be("application/json");

        ReturnPooledBuffers(pending);
    }

    [Fact]
    public async Task TransactionalOutbox_DuplicateMessageId_SkippedByInbox()
    {
        // Arrange
        Guid messageId = Guid.NewGuid();
        IServiceProvider sp = Substitute.For<IServiceProvider>();
        var headers = new Dictionary<string, string> { ["BW-MessageType"] = "OrderCreated" };

        byte[] body = Encoding.UTF8.GetBytes("{}");
        var outboundMessage = new OutboundMessage(
            "payments.requested",
            new Dictionary<string, string>(),
            body,
            "application/json");

        int handlerCallCount = 0;

        NextMiddleware handler = ctx =>
        {
            handlerCallCount++;
            TransactionalOutboxMiddleware.Current?.Add(outboundMessage);
            return Task.CompletedTask;
        };

        // Process first time — should succeed and run the handler
        var context1 = new MessageContext(messageId, headers, ReadOnlySequence<byte>.Empty, sp);
        await _middleware.InvokeAsync(context1, handler);

        // Recreate with a fresh DbContext to avoid change tracker conflicts from the
        // inbox store's own SaveChangesAsync call.
        await _dbContext.DisposeAsync();
        _dbContext = CreateDbContext();
        RebuildComponents();

        // Act — second invocation with the same messageId
        var context2 = new MessageContext(messageId, headers, ReadOnlySequence<byte>.Empty, sp);
        await _middleware.InvokeAsync(context2, handler);

        // Assert — handler ran exactly once; duplicate was skipped by the inbox filter
        handlerCallCount.Should().Be(1);

        _dbContext.ChangeTracker.Clear();
        IReadOnlyList<OutboxEntry> pending = await _outboxStore.GetPendingAsync(100, CancellationToken.None);

        pending.Should().HaveCount(1, "only the first invocation should have produced an outbox record");
        ReturnPooledBuffers(pending);
    }

    [Fact]
    public async Task TransactionalOutbox_TwoConsumersSameMessageType_BothProcessed()
    {
        // Arrange — two endpoints consuming the same message type but bound to different queues.
        // They produce separate inbox keys because EndpointName differs.
        Guid messageId = Guid.NewGuid();
        IServiceProvider sp = Substitute.For<IServiceProvider>();
        var headers = new Dictionary<string, string> { ["BW-MessageType"] = "OrderCreated" };

        int handlerCallCount = 0;

        NextMiddleware handler = ctx =>
        {
            handlerCallCount++;
            return Task.CompletedTask;
        };

        // First consumer: payment-email endpoint
        var context1 = new MessageContext(messageId, headers, ReadOnlySequence<byte>.Empty, sp, endpointName: "payment-email");
        await _middleware.InvokeAsync(context1, handler);

        // Recreate DbContext between invocations — same pattern as TransactionalOutbox_DuplicateMessageId_SkippedByInbox
        await _dbContext.DisposeAsync();
        _dbContext = CreateDbContext();
        RebuildComponents();

        // Second consumer: payment-audit endpoint — same MessageId, same BW-MessageType, different EndpointName
        var context2 = new MessageContext(messageId, headers, ReadOnlySequence<byte>.Empty, sp, endpointName: "payment-audit");
        await _middleware.InvokeAsync(context2, handler);

        // Assert — both handlers should have run because (MessageId, EndpointName) is distinct per consumer
        handlerCallCount.Should().Be(2, "different endpoints produce different inbox keys even for the same MessageId");
    }

    [Fact]
    public async Task TransactionalOutbox_AfterSuccessfulProcessing_MarksProcessed()
    {
        // Arrange
        Guid messageId = Guid.NewGuid();
        IServiceProvider sp = Substitute.For<IServiceProvider>();
        var headers = new Dictionary<string, string> { ["BW-MessageType"] = "OrderCreated" };
        var context = new MessageContext(messageId, headers, ReadOnlySequence<byte>.Empty, sp, endpointName: "payment-email");

        NextMiddleware handler = ctx => Task.CompletedTask;

        // Act
        await _middleware.InvokeAsync(context, handler);

        // Assert — query InboxMessages directly; InboxMessage is internal but InternalsVisibleTo is set
        _dbContext.ChangeTracker.Clear();

        InboxMessage? entry = await _dbContext.InboxMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MessageId == messageId && m.ConsumerType == "payment-email");

        entry.Should().NotBeNull("the middleware must record an inbox entry after successful processing");
        entry!.ProcessedAt.Should().NotBeNull("successful processing must set ProcessedAt to a non-null timestamp");
    }

    [Fact]
    public async Task TransactionalOutbox_HandlerException_RollbackNoOutboxRecords()
    {
        // Arrange
        Guid messageId = Guid.NewGuid();
        IServiceProvider sp = Substitute.For<IServiceProvider>();
        var headers = new Dictionary<string, string> { ["BW-MessageType"] = "OrderCreated" };
        var context = new MessageContext(messageId, headers, ReadOnlySequence<byte>.Empty, sp);

        byte[] body = Encoding.UTF8.GetBytes("{}");
        var outboundMessage = new OutboundMessage(
            "payments.requested",
            new Dictionary<string, string>(),
            body,
            "application/json");

        // Handler adds a message to the buffer, then faults — simulates partial work before failure
        NextMiddleware faultyHandler = ctx =>
        {
            TransactionalOutboxMiddleware.Current?.Add(outboundMessage);
            throw new InvalidOperationException("Simulated handler failure");
        };

        // Act
        Func<Task> act = () => _middleware.InvokeAsync(context, faultyHandler);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Simulated handler failure");

        // Assert — middleware catches and clears the buffer; SaveChangesAsync is never called
        // so no outbox entries exist (SQLite cannot rollback via TransactionScope, but
        // the middleware guarantees SaveChangesAsync is not called on the outbox DbContext).
        _dbContext.ChangeTracker.Clear();
        IReadOnlyList<OutboxEntry> pending = await _outboxStore.GetPendingAsync(100, CancellationToken.None);

        pending.Should().BeEmpty("outbox entries must not be saved when the handler throws");
    }

    private static void ReturnPooledBuffers(IReadOnlyList<OutboxEntry> entries)
    {
        foreach (OutboxEntry entry in entries)
        {
            ArrayPool<byte>.Shared.Return(entry.PooledBody);
        }
    }
}
