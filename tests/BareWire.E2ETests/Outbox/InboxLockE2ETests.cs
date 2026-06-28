using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AwesomeAssertions;
using BareWire.Outbox.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BareWire.E2ETests.Outbox;

/// <summary>
/// E2E tests for <see cref="EfCoreInboxStore.TryLockAsync"/> re-lock semantics against a real
/// PostgreSQL instance. The atomicity guarantee these tests prove — exactly one worker may
/// re-acquire an expired inbox lock — only manifests under genuine concurrency with independent
/// database connections, so SQLite/in-memory cannot reproduce it.
/// </summary>
/// <remarks>
/// Tests are annotated with the <c>requires-postgres</c> trait so they can be filtered in CI.
/// If Docker is unavailable the fixture initialization will throw and tests will show as failed
/// with a clear message rather than silently passing. Reuses the same minimal Aspire AppHost
/// (a single PostgreSQL resource) as <see cref="OutboxClaimE2ETests"/>.
/// </remarks>
[Trait("Category", "requires-postgres")]
public sealed class InboxLockE2ETests : IAsyncLifetime
{
    private DistributedApplication? _app;
    private string? _connectionString;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

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

    private OutboxDbContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<OutboxDbContext>();
        optionsBuilder.UseNpgsql(_connectionString!);
        return new OutboxDbContext(optionsBuilder.Options);
    }

    private static EfCoreInboxStore CreateStore(OutboxDbContext context)
        => new(context, new PostgresInboxSqlDialect());

    // ── Test: concurrent re-lock of an expired row yields exactly one winner ───

    /// <summary>
    /// Two workers racing to re-lock the SAME expired, unprocessed inbox row must not both win:
    /// the re-lock has to be a single atomic winner-takes-lock operation. A double-win would let
    /// two consumers process the same logical message, breaking the inbox dedup guarantee precisely
    /// on the slow-handler / redelivery boundary where the protection matters most.
    /// </summary>
    [Fact]
    public async Task TryLockAsync_WhenTwoWorkersRaceOnExpiredRow_ExactlyOneWins()
    {
        // Arrange — fresh schema + a seeded EXPIRED, UNPROCESSED row (a stale lock from a crashed
        // or slow worker that is now eligible for re-acquisition).
        await using OutboxDbContext seedContext = CreateDbContext();
        await seedContext.Database.EnsureCreatedAsync();

        Guid messageId = Guid.NewGuid();
        const string consumerType = "RaceConsumer";

        DateTimeOffset now = DateTimeOffset.UtcNow;
        seedContext.InboxMessages.Add(new InboxMessage
        {
            MessageId = messageId,
            ConsumerType = consumerType,
            ReceivedAt = now - TimeSpan.FromMinutes(5),
            ExpiresAt = now - TimeSpan.FromSeconds(1),
            ProcessedAt = null,
        });
        await seedContext.SaveChangesAsync();

        // Two independent contexts/stores (separate connections) racing on the same row.
        await using OutboxDbContext contextA = CreateDbContext();
        await using OutboxDbContext contextB = CreateDbContext();
        EfCoreInboxStore storeA = CreateStore(contextA);
        EfCoreInboxStore storeB = CreateStore(contextB);

        TimeSpan lockTimeout = TimeSpan.FromSeconds(30);

        // Act — concurrent re-lock attempts.
        bool[] results = await Task.WhenAll(
            storeA.TryLockAsync(messageId, consumerType, lockTimeout, CancellationToken.None).AsTask(),
            storeB.TryLockAsync(messageId, consumerType, lockTimeout, CancellationToken.None).AsTask());

        // Assert — exactly one worker may re-acquire the expired lock.
        results.Count(won => won).Should().Be(1,
            "exactly one worker may re-acquire an expired inbox lock; a double-win lets two consumers " +
            "process the same message and breaks the inbox dedup guarantee");
    }

    // ── Test: a fresh message is always lockable exactly once ──────────────────

    /// <summary>
    /// Two workers racing to lock a brand-new (not-yet-seen) message must also yield exactly one
    /// winner — the upsert path is the first line of dedup defense and must be atomic too.
    /// </summary>
    [Fact]
    public async Task TryLockAsync_WhenTwoWorkersRaceOnNewMessage_ExactlyOneWins()
    {
        await using OutboxDbContext seedContext = CreateDbContext();
        await seedContext.Database.EnsureCreatedAsync();

        Guid messageId = Guid.NewGuid();
        const string consumerType = "NewRaceConsumer";

        await using OutboxDbContext contextA = CreateDbContext();
        await using OutboxDbContext contextB = CreateDbContext();
        EfCoreInboxStore storeA = CreateStore(contextA);
        EfCoreInboxStore storeB = CreateStore(contextB);

        TimeSpan lockTimeout = TimeSpan.FromSeconds(30);

        bool[] results = await Task.WhenAll(
            storeA.TryLockAsync(messageId, consumerType, lockTimeout, CancellationToken.None).AsTask(),
            storeB.TryLockAsync(messageId, consumerType, lockTimeout, CancellationToken.None).AsTask());

        results.Count(won => won).Should().Be(1,
            "a brand-new message may be locked by exactly one worker — the upsert dedup must be atomic");
    }
}
