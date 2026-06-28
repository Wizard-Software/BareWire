using System.Buffers;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AwesomeAssertions;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BareWire.E2ETests.Outbox;

/// <summary>
/// E2E tests proving that the inbox processed marker is written atomically with the business
/// effect (outbox rows) inside a single <see cref="System.Transactions.TransactionScope"/>.
/// A crash (or injected failure) between <c>SaveChangesAsync</c> and <c>MarkProcessedAsync</c>
/// must roll back the business writes — not leave a committed-but-unprocessed window that lets
/// the message be reprocessed after <c>ExpiresAt</c>.
/// </summary>
/// <remarks>
/// Requires a real PostgreSQL instance — SQLite ignores <c>TransactionScope</c> and cannot
/// demonstrate the atomicity guarantee. Tests are annotated with <c>requires-postgres</c>
/// so they can be filtered in CI when Docker is unavailable.
/// </remarks>
[Trait("Category", "requires-postgres")]
public sealed class TransactionalInboxAtomicityE2ETests : IAsyncLifetime
{
    private DistributedApplication? _app;
    private string? _connectionString;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Short lock timeout so the re-delivery window is testable without long waits.</summary>
    private static readonly TimeSpan ShortInboxLockTimeout = TimeSpan.FromSeconds(4);

    public async ValueTask InitializeAsync()
    {
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
        var ob = new DbContextOptionsBuilder<OutboxDbContext>();
        ob.UseNpgsql(_connectionString!);
        return new OutboxDbContext(ob.Options);
    }

    private static OutboxOptions CreateOptions() => new()
    {
        InboxLockTimeout = ShortInboxLockTimeout,
        InboxRetention = TimeSpan.FromDays(7),
        OutboxLockTimeout = TimeSpan.FromSeconds(15),
        OutboxRetention = TimeSpan.FromDays(7),
        PollingInterval = TimeSpan.FromSeconds(1),
        CleanupInterval = TimeSpan.FromHours(1)
    };

    private static (TransactionalOutboxMiddleware Middleware, EfCoreOutboxStore OutboxStore)
        CreateMiddleware(OutboxDbContext dbContext, IInboxStore inboxStore, OutboxOptions options)
    {
        var outboxStore = new EfCoreOutboxStore(
            dbContext,
            new OutboxInstanceId("atomicity-test"),
            new PostgresOutboxSqlDialect(),
            options);

        var inboxFilter = new InboxFilter(
            inboxStore,
            options,
            NullLogger<InboxFilter>.Instance);

        var middleware = new TransactionalOutboxMiddleware(
            dbContext,
            outboxStore,
            inboxFilter,
            NullLogger<TransactionalOutboxMiddleware>.Instance);

        return (middleware, outboxStore);
    }

    /// <summary>
    /// Creates a <see cref="NextMiddleware"/> delegate that adds one outbound message to the
    /// active <see cref="OutboxBuffer"/> — simulating a handler that publishes one event.
    /// The routing key is unique per test run so counts can be scoped to this test.
    /// </summary>
    private static NextMiddleware CreateBufferingDelegate(string destinationAddress) =>
        _ =>
        {
            TransactionalOutboxMiddleware.Current?.Add(new OutboundMessage(
                routingKey: destinationAddress,
                headers: new Dictionary<string, string>(),
                body: new ReadOnlyMemory<byte>([1]),
                contentType: "application/json"));
            return Task.CompletedTask;
        };

    private static MessageContext CreateContext(Guid messageId, string endpointName) =>
        new(messageId,
            headers: new Dictionary<string, string>(),
            rawBody: ReadOnlySequence<byte>.Empty,
            serviceProvider: new ServiceCollection().BuildServiceProvider(),
            endpointName: endpointName);

    // ── Fail-once IInboxStore decorator ──────────────────────────────────────

    /// <summary>
    /// Wraps an <see cref="IInboxStore"/> and throws <see cref="InvalidOperationException"/>
    /// on the first <c>MarkProcessedAsync</c> call only; all subsequent calls delegate to the inner store.
    /// A shared <c>int[]</c> lets multiple decorator instances (one per test attempt) share the
    /// call counter so that the fail-once semantics hold across DbContext lifetimes.
    /// </summary>
    private sealed class FailOnceMarkProcessedDecorator : IInboxStore
    {
        private readonly IInboxStore _inner;
        private readonly int[] _sharedCallCount;

        internal FailOnceMarkProcessedDecorator(IInboxStore inner, int[] sharedCallCount)
        {
            _inner = inner;
            _sharedCallCount = sharedCallCount;
        }

        public ValueTask<bool> TryLockAsync(
            Guid messageId,
            string consumerType,
            TimeSpan lockTimeout,
            CancellationToken cancellationToken = default)
            => _inner.TryLockAsync(messageId, consumerType, lockTimeout, cancellationToken);

        public ValueTask MarkProcessedAsync(
            Guid messageId,
            string consumerType,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _sharedCallCount[0]) == 1)
            {
                throw new InvalidOperationException(
                    "Simulated MarkProcessedAsync fail-once failure: injected to prove atomicity.");
            }

            return _inner.MarkProcessedAsync(messageId, consumerType, cancellationToken);
        }

        public ValueTask CleanupAsync(TimeSpan retention, CancellationToken cancellationToken = default)
            => _inner.CleanupAsync(retention, cancellationToken);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// When <c>MarkProcessedAsync</c> fails (simulating a crash or network fault between the
    /// business commit and the processed marker), the business effect must be applied exactly
    /// once — not twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the <em>unfixed</em> code, <c>scope.Complete()</c> runs before <c>MarkProcessedAsync</c>
    /// (inside a separate Suppress scope). An injected failure there leaves the OutboxMessage
    /// committed but <c>ProcessedAt</c> unset. After <c>ExpiresAt</c>, re-delivery re-runs the
    /// handler, commits a second OutboxMessage, so the final count == 2 → RED.
    /// </para>
    /// <para>
    /// After the fix, <c>MarkProcessedAsync</c> runs inside the same ambient
    /// <c>TransactionScope</c> before <c>scope.Complete()</c>. The injected failure rolls back
    /// the entire transaction (business + outbox + processed marker). Re-delivery commits exactly
    /// once → count == 1 → GREEN.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Process_WhenMarkProcessedFailsAfterBusinessCommit_AppliesBusinessEffectExactlyOnce()
    {
        // Arrange — ensure schema exists and create unique identifiers for this test run.
        await using OutboxDbContext setupContext = CreateDbContext();
        await setupContext.Database.EnsureCreatedAsync();

        string runId = Guid.NewGuid().ToString("N");
        string destinationAddress = $"test.atomicity.{runId}";
        string endpointName = $"atomicity-{runId}";
        Guid messageId = Guid.NewGuid();

        OutboxOptions options = CreateOptions();

        // Shared counter: call 1 (attempt 1) throws; call 2 (attempt 2) delegates.
        int[] sharedCallCount = [0];

        // Attempt 1 — inject MarkProcessed failure to simulate a crash in the at-least-once window.
        // Unfixed:  scope.Complete() already ran → OutboxMessage IS committed (count += 1 after this).
        // Fixed:    MarkProcessed is inside the scope → entire tx rolls back → count stays 0.
        await using (OutboxDbContext ctx1 = CreateDbContext())
        {
            var inboxStore1 = new FailOnceMarkProcessedDecorator(
                new EfCoreInboxStore(ctx1, new PostgresInboxSqlDialect()),
                sharedCallCount);

            var (middleware, _) = CreateMiddleware(ctx1, inboxStore1, options);
            NextMiddleware next = CreateBufferingDelegate(destinationAddress);
            MessageContext msgCtx = CreateContext(messageId, endpointName);

            bool threw = false;
            try
            {
                await middleware.InvokeAsync(msgCtx, next);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            threw.Should().BeTrue(
                "the fail-once decorator must propagate the injected exception out of attempt 1");
        }

        // Wait for the inbox lock to expire so attempt 2 can re-acquire it.
        await Task.Delay(ShortInboxLockTimeout + TimeSpan.FromSeconds(2));

        // Attempt 2 — re-delivery of the same messageId.
        // sharedCallCount[0] is now 1, so the next increment (== 2) does NOT throw → delegates.
        // Both paths (unfixed and fixed) must succeed here and commit one OutboxMessage.
        await using (OutboxDbContext ctx2 = CreateDbContext())
        {
            var inboxStore2 = new FailOnceMarkProcessedDecorator(
                new EfCoreInboxStore(ctx2, new PostgresInboxSqlDialect()),
                sharedCallCount);

            var (middleware, _) = CreateMiddleware(ctx2, inboxStore2, options);
            NextMiddleware next = CreateBufferingDelegate(destinationAddress);
            MessageContext msgCtx = CreateContext(messageId, endpointName);

            await middleware.InvokeAsync(msgCtx, next);
        }

        // Assert — exactly one outbox row must exist for this test run's routing address.
        //
        // RED  (unfixed code): count == 2 — attempt 1 committed the business effect even though
        //                      MarkProcessed failed, so attempt 2 adds a duplicate → 2 rows.
        // GREEN (fixed code):  count == 1 — attempt 1 rolled back everything; attempt 2 committed
        //                      exactly once → 1 row.
        await using OutboxDbContext verifyContext = CreateDbContext();

        int outboxRowCount = await verifyContext.OutboxMessages
            .AsNoTracking()
            .CountAsync(m => m.DestinationAddress == destinationAddress);

        outboxRowCount.Should().Be(1,
            "the business effect (outbox row) must be committed exactly once — " +
            "the at-least-once reprocessing window is closed when MarkProcessedAsync is atomic " +
            "with the business state inside the same TransactionScope");

        // Secondary assertion: ProcessedAt must be non-null after the successful commit.
        // Ensures there is no committed-but-unprocessed state window observable in the DB.
        InboxMessage? inboxRow = await verifyContext.InboxMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ConsumerType == endpointName);

        inboxRow.Should().NotBeNull("the inbox row must exist after successful processing");
        inboxRow!.ProcessedAt.Should().NotBeNull(
            "ProcessedAt must be set atomically with the business commit — " +
            "no committed-but-unprocessed window must exist after a successful run");
    }
}
