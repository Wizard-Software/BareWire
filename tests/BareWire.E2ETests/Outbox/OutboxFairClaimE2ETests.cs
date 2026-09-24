using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BareWire.E2ETests.Outbox;

/// <summary>
/// E2E tests for the fair two-class claim against a real PostgreSQL instance: the single-class
/// claim statements (<see cref="PostgresOutboxSqlDialect.GetNewRowsClaimSql"/> and
/// <see cref="PostgresOutboxSqlDialect.GetDueOrderedRetryClaimSql"/>) must use an ordered index scan
/// on a realistic-size table, and the carry-forward rule must hold end to end.
/// </summary>
// Shares the outbox schema (and its optional ordering index) with the other claim E2E class, so both
// run in one collection instead of in parallel against the same database.
[Collection("OutboxClaimSchema")]
[Trait("Category", "requires-postgres")]
public sealed class OutboxFairClaimE2ETests : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan TestLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex SortNodeRegex = new(@"->\s+(Incremental )?Sort\b");

    private DistributedApplication? _app;
    private string? _connectionString;

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

    // ── Claim SQL must use the ordered index, never a Sort ────────────────────

    /// <summary>
    /// Against a ~100 000-row table, every statement of the three-step claim cycle — new rows ("new"),
    /// due retries ("due") and the top-up with new rows ("topup") — must be served by an ordered scan
    /// of <c>IX_OutboxMessages_Claim</c> without a Sort node — the whole reason those statements exist
    /// instead of the combined public claim statement, whose OR predicate the planner cannot serve
    /// from that index in order.
    /// </summary>
    /// <remarks>
    /// With the built-in dialect the top-up step runs the same new-rows statement as the first step,
    /// only with the smaller top-up limit, so it is planned here with such a limit. A plain EXPLAIN
    /// plans from the table statistics and the parameter values, not from rows claimed earlier in the
    /// same cycle.
    /// </remarks>
    [Theory]
    [InlineData("new", OrderingMode.None)]
    [InlineData("new", OrderingMode.PerKey)]
    [InlineData("due", OrderingMode.None)]
    [InlineData("due", OrderingMode.PerKey)]
    [InlineData("topup", OrderingMode.None)]
    [InlineData("topup", OrderingMode.PerKey)]
    public async Task ClaimSql_HundredThousandRows_UsesOrderedClaimIndexScanWithoutSort(string shape, OrderingMode mode)
    {
        string marker = $"fair-claim-explain-{Guid.NewGuid():N}";
        OutboxOptions options = CreateOptions();

        await using OutboxDbContext seedContext = mode == OrderingMode.PerKey
            ? await CreateDbContextWithOrderingAsync(options)
            : CreateDbContext();
        await seedContext.Database.EnsureCreatedAsync();

        try
        {
            await SeedBulkClaimRowsAsync(seedContext, marker, mode == OrderingMode.PerKey);
            await seedContext.Database.ExecuteSqlRawAsync("ANALYZE \"OutboxMessages\"");

            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset staleCutoff = now - options.OutboxLockTimeout;
            var dialect = new PostgresOutboxSqlDialect();

            // The top-up limit is the capacity left after the first two steps, always a small remainder
            // of the batch.
            FormattableString sql = shape switch
            {
                "new" => dialect.GetNewRowsClaimSql("explain-instance", now, 100, mode),
                "due" => dialect.GetDueOrderedRetryClaimSql("explain-instance", now, staleCutoff, 100, mode),
                "topup" => dialect.GetNewRowsClaimSql("explain-instance", now, 7, mode),
                _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown claim step shape."),
            };

            IReadOnlyList<string> planLines = await RenderExplainPlanAsync(seedContext, sql);
            string planText = string.Join('\n', planLines);

            bool hasPlainIndexScan = planLines.Any(line =>
            {
                string trimmed = line.TrimStart();
                bool mentionsIndex =
                    trimmed.Contains("Index Scan using \"IX_OutboxMessages_Claim\"", StringComparison.Ordinal)
                    || trimmed.Contains("Index Scan using IX_OutboxMessages_Claim", StringComparison.Ordinal);
                return mentionsIndex && !trimmed.Contains("Bitmap", StringComparison.Ordinal);
            });

            hasPlainIndexScan.Should().BeTrue(
                $"the {shape} claim statement must use an ordered index scan on IX_OutboxMessages_Claim, " +
                $"not a bitmap or sequential scan; plan:\n{planText}");

            bool hasSortNode = planLines.Any(line =>
                SortNodeRegex.IsMatch(line) || line.TrimStart().StartsWith("Sort", StringComparison.Ordinal));

            hasSortNode.Should().BeFalse(
                $"the {shape} claim statement must not require a Sort node; plan:\n{planText}");

            bool sortKeyMentionsClaimColumns = planLines
                .Where(line => line.Contains("Sort Key:", StringComparison.Ordinal))
                .Any(line =>
                    line.Contains("LockedAt", StringComparison.Ordinal)
                    || line.Contains("\"Id\"", StringComparison.Ordinal));

            sortKeyMentionsClaimColumns.Should().BeFalse(
                $"no Sort Key should reference LockedAt or Id; plan:\n{planText}");
        }
        finally
        {
            await MarkMarkedRowsDeliveredAsync(seedContext, marker);
        }
    }

    // ── Carry-forward end to end ───────────────────────────────────────────────

    /// <summary>
    /// Claiming twice in a row without releasing must return exactly the same rows the second time —
    /// carry-forward must cap the effective batch, never letting one instance own more than
    /// <c>batchSize</c> undelivered rows.
    /// </summary>
    [Fact]
    public async Task GetPendingAsync_CarryForwardAfterFailedSend_DoesNotClaimBeyondBatchSize()
    {
        await using OutboxDbContext seedContext = CreateDbContext();
        await seedContext.Database.EnsureCreatedAsync();

        string instanceId = $"fair-claim-carry-{Guid.NewGuid():N}";
        string marker = $"fair-claim-carry-{Guid.NewGuid():N}";
        await SeedMarkedRowsAsync(seedContext, marker, count: 10);

        OutboxOptions options = CreateOptions(lockTimeout: TimeSpan.FromSeconds(30));
        await using OutboxDbContext claimContext = CreateDbContext();
        EfCoreOutboxStore store = CreateStore(claimContext, instanceId, options);

        IReadOnlyList<OutboxEntry> first = await store.GetPendingAsync(4, CancellationToken.None);
        IReadOnlyList<OutboxEntry> second = Array.Empty<OutboxEntry>();
        try
        {
            first.Should().HaveCount(4);
            HashSet<long> firstIds = first.Select(e => e.Id).ToHashSet();

            second = await store.GetPendingAsync(4, CancellationToken.None);
            second.Should().HaveCount(4);
            second.Select(e => e.Id).ToHashSet().Should().BeEquivalentTo(firstIds,
                "carry-forward must reclaim the same rows this instance already owns, not new ones");

            await using OutboxDbContext verifyContext = CreateDbContext();
            int ownedCount = await verifyContext.OutboxMessages
                .AsNoTracking()
                .CountAsync(m => m.LockedBy == instanceId && m.DeliveredAt == null);
            ownedCount.Should().Be(4, "this instance must not own more than batchSize rows after two claim cycles");
        }
        finally
        {
            ReturnBuffers(first);
            ReturnBuffers(second);
            await MarkMarkedRowsDeliveredAsync(seedContext, marker);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private OutboxDbContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<OutboxDbContext>();
        optionsBuilder.UseNpgsql(_connectionString!);
        return new OutboxDbContext(optionsBuilder.Options);
    }

    private static EfCoreOutboxStore CreateStore(OutboxDbContext context, string instanceId, OutboxOptions options)
        => new(context, new OutboxInstanceId(instanceId), new PostgresOutboxSqlDialect(), options);

    private static OutboxOptions CreateOptions(TimeSpan? lockTimeout = null)
        => new()
        {
            PollingInterval = TimeSpan.FromSeconds(1),
            OutboxLockTimeout = lockTimeout ?? TestLockTimeout,
            OutboxRetention = TimeSpan.FromDays(7),
            InboxRetention = TimeSpan.FromDays(8),
            InboxLockTimeout = TimeSpan.FromSeconds(30),
        };

    // Creates a DbContext that includes the ordering model customizer so schema creation via
    // EnsureCreatedAsync also creates IX_OutboxMessages_Ordering, and makes sure that index exists
    // even when the table was already created earlier (by this class or a sibling) without it.
    private async Task<OutboxDbContext> CreateDbContextWithOrderingAsync(OutboxOptions perKeyOptions)
    {
        var ob = new DbContextOptionsBuilder<OutboxDbContext>();
        ob.UseNpgsql(_connectionString!);
        ((IDbContextOptionsBuilderInfrastructure)ob).AddOrUpdateExtension(
            new OutboxModelCustomizerExtension(perKeyOptions));
        var context = new OutboxDbContext(ob.Options);

        await context.Database.EnsureCreatedAsync();
        await EnsureOrderingIndexExistsAsync(context);

        return context;
    }

    private static async Task EnsureOrderingIndexExistsAsync(OutboxDbContext context)
    {
        const string indexName = "IX_OutboxMessages_Ordering";

        bool indexExists = await context.Database
            .SqlQuery<int>($"SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = {indexName}")
            .AnyAsync();

        if (!indexExists)
        {
            await context.Database.ExecuteSqlRawAsync(
                """CREATE INDEX IF NOT EXISTS "IX_OutboxMessages_Ordering" ON "OutboxMessages" ("OrderingKey", "Id") WHERE "DeliveredAt" IS NULL""");
        }
    }

    // Bulk-seeds ~100 000 undelivered rows for the EXPLAIN test: half unclaimed (LockedAt NULL, the
    // "new" class), half stale-locked (LockedAt in the past, LockedBy NULL, the "due retry" class).
    // With perKey, ~1 000 distinct keys are assigned, with roughly every 100th row left keyless.
    // Scoped to a single caller-chosen marker so the seeded rows can be found and cleaned up
    // precisely, without ever truncating the shared table.
    private static async Task SeedBulkClaimRowsAsync(OutboxDbContext context, string marker, bool perKey)
    {
        const int rowCount = 100_000;

        string orderingKeyExpression = perKey
            ? $"CASE WHEN g % 100 = 0 THEN NULL ELSE '{marker}-key-' || (g % 1000) END"
            : "NULL";

        string sql = $"""
            INSERT INTO "OutboxMessages"
                ("MessageId", "DestinationAddress", "ContentType", "Payload", "CreatedAt", "RetryCount", "LockedAt", "LockedBy", "OrderingKey")
            SELECT
                ('00000000-0000-0000-0000-' || lpad(g::text, 12, '0'))::uuid,
                '{marker}.' || g,
                'application/json',
                '\x00'::bytea,
                now(),
                0,
                CASE WHEN g % 2 = 0 THEN NULL ELSE now() - (g || ' seconds')::interval END,
                NULL,
                {orderingKeyExpression}
            FROM generate_series(1, {rowCount}) AS g
            """;

        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task SeedMarkedRowsAsync(OutboxDbContext context, string marker, int count)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < count; i++)
        {
            context.OutboxMessages.Add(new OutboxMessage
            {
                MessageId = Guid.NewGuid(),
                DestinationAddress = $"{marker}.{i}",
                ContentType = "application/json",
                Payload = [1, 2, 3],
                CreatedAt = now,
            });
        }

        await context.SaveChangesAsync();
    }

    // Cleans up rows this test seeded so they stop being claimable by sibling E2E tests sharing the
    // same database — never truncated, only marked delivered.
    private static async Task MarkMarkedRowsDeliveredAsync(OutboxDbContext context, string marker)
    {
        await context.Database.ExecuteSqlAsync(
            $"""UPDATE "OutboxMessages" SET "DeliveredAt" = now() WHERE "DestinationAddress" LIKE {marker + "%"} AND "DeliveredAt" IS NULL""");
    }

    // Renders a claim FormattableString with its argument holes replaced by ADO.NET parameter
    // placeholders, then runs a plain EXPLAIN (no ANALYZE — the statement is an UPDATE and must never
    // actually execute) to obtain the planner's chosen shape without mutating any row.
    private static async Task<IReadOnlyList<string>> RenderExplainPlanAsync(OutboxDbContext context, FormattableString sql)
    {
        object?[] arguments = sql.GetArguments();
        var placeholders = new object[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            placeholders[i] = $"@p{i}";
        }

        string rendered = string.Format(CultureInfo.InvariantCulture, sql.Format, placeholders);
        string explainSql = $"EXPLAIN (FORMAT TEXT) {rendered}";

        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = explainSql;

        for (int i = 0; i < arguments.Length; i++)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = $"p{i}";
            parameter.Value = arguments[i] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var lines = new List<string>();
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }

    private static void ReturnBuffers(IReadOnlyList<OutboxEntry> entries)
    {
        foreach (OutboxEntry entry in entries)
        {
            ArrayPool<byte>.Shared.Return(entry.PooledBody);
        }
    }
}
