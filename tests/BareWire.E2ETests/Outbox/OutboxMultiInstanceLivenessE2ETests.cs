using System.Buffers;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BareWire.E2ETests.Outbox;

/// <summary>
/// E2E liveness and exactly-once tests for the fair two-class claim of <see cref="EfCoreOutboxStore"/>
/// against a real PostgreSQL instance: two competing instances with nacks and deferral, takeover of
/// abandoned claims behind a permanently rejected cohort, and no starvation of new rows.
/// </summary>
/// <remarks>
/// Every instance in these tests uses the same <c>OutboxLockTimeout</c>. The stale-claim cutoff is
/// judged on the claiming instance's clock, and marking a row delivered does not check the lock
/// owner, so exactly-once delivery across instances holds only while every instance agrees on that
/// timeout.
/// <para>
/// Each test runs against its own database, created in <see cref="InitializeAsync"/> and dropped in
/// <see cref="DisposeAsync"/>. The store claims every undelivered row in the table, so a row left
/// behind by an unrelated test sharing the same database would silently inflate the cycle counts and
/// delivery ledgers these tests measure — the isolated database removes that dependency regardless of
/// how the shared PostgreSQL container's lifetime or test scheduling evolve.
/// </para>
/// </remarks>
[Collection("OutboxClaimSchema")]
[Trait("Category", "requires-postgres")]
public sealed class OutboxMultiInstanceLivenessE2ETests : IAsyncLifetime
{
    private const int Batch = 8;
    private const int RetryShare = 2; // max(1, Batch / 4) — hard-coded here, never read from production.
    private const int NewShare = Batch - RetryShare;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    private DistributedApplication? _app;
    private string? _adminConnectionString; // Connection string of the shared session database "outbox-db".
    private string? _connectionString; // Connection string of this test's isolated database.
    private string? _databaseName;

    public async ValueTask InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.BareWire_OutboxE2EAppHost>();

        _app = await builder.BuildAsync();

        var notifier = _app.Services.GetRequiredService<ResourceNotificationService>();
        await _app.StartAsync();

        using var cts = new CancellationTokenSource(StartupTimeout);
        await notifier.WaitForResourceHealthyAsync("outbox-pg", cts.Token);

        _adminConnectionString = await _app.GetConnectionStringAsync("outbox-db", cts.Token);

        // Assigned right before CREATE DATABASE runs, so DisposeAsync's guard is meaningful even if
        // CREATE itself throws — DROP DATABASE IF EXISTS is then a safe no-op against a database that
        // was never created.
        _databaseName = $"outbox_p0_{Guid.NewGuid():N}";

        // Database DDL cannot run inside a transaction block and its target name cannot be a bound
        // parameter, so this runs as a plain ADO.NET command over an explicit connection rather than
        // through EF Core's ExecuteSqlRawAsync (would trip the analyzer that flags raw interpolated
        // SQL) or ExecuteSqlAsync (would try to bind the name as a parameter, which PostgreSQL DDL
        // rejects for an identifier).
        string createDatabaseSql = $"CREATE DATABASE \"{_databaseName}\"";
        await using (var adminConnection = new NpgsqlConnection(_adminConnectionString))
        {
            await adminConnection.OpenAsync(cts.Token);
            await using NpgsqlCommand command = adminConnection.CreateCommand();
            command.CommandText = createDatabaseSql;
            await command.ExecuteNonQueryAsync(cts.Token);
        }

        _connectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName
        }.ConnectionString;

        await using OutboxDbContext context = CreateDbContext();
        await context.Database.EnsureCreatedAsync(cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Only ever drops a database this test created itself, identified by its own prefix —
            // never the shared session database "outbox-db".
            if (_adminConnectionString is not null
                && _databaseName is not null
                && _databaseName.StartsWith("outbox_p0_", StringComparison.Ordinal))
            {
                if (_connectionString is not null)
                {
                    NpgsqlConnection.ClearPool(new NpgsqlConnection(_connectionString));
                }

                string dropDatabaseSql = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
                await using var adminConnection = new NpgsqlConnection(_adminConnectionString);
                await adminConnection.OpenAsync();
                await using NpgsqlCommand command = adminConnection.CreateCommand();
                command.CommandText = dropDatabaseSql;
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }
    }

    // ── Test 1: two instances, nacks, deferral — exactly-once delivery ────────

    /// <summary>
    /// Two instances repeatedly claim, nack, and eventually confirm rows from a shared backlog on a
    /// manually advanced clock. No row may ever be delivered twice, and neither instance's release can
    /// affect the other's claim.
    /// </summary>
    [Fact]
    public async Task GetPendingAsync_TwoInstancesWithNacksAndDeferral_DeliversEveryRowExactlyOnce()
    {
        // Uniform lock timeout: both instances are configured from the same factory call, and the test
        // asserts they agree — exactly-once delivery across instances assumes it (see class remarks).
        OutboxOptions optionsA = CreateOptions(lockTimeout: TimeSpan.FromSeconds(30));
        OutboxOptions optionsB = CreateOptions(lockTimeout: TimeSpan.FromSeconds(30));
        optionsA.OutboxLockTimeout.Should().Be(optionsB.OutboxLockTimeout,
            "exactly-once delivery across instances assumes one OutboxLockTimeout for every instance");

        var clock = new ManualClock(DateTimeOffset.UtcNow);
        IReadOnlyList<long> ids = await SeedRowsAsync("p0-two-instances", count: 60, clock);

        // Scripted transport: rows with Id % 3 == 0 are rejected on their first attempt, rows with
        // Id % 6 == 0 also on their second; every other attempt is confirmed.
        static bool Accept(OutboxEntry e) => e.Id % 3 != 0 ? true : e.Id % 6 == 0 ? e.NackCount >= 2 : e.NackCount >= 1;

        var ledger = new DeliveryLedger();
        int cycle = 0;
        while (await CountUndeliveredAsync(ids) > 0 && cycle < 60)
        {
            cycle++;
            clock.Advance(optionsA.PollingInterval);

            // (a) concurrent claims on two connections — proves the FOR UPDATE SKIP LOCKED claim is atomic.
            (OutboxDbContext Context, EfCoreOutboxStore Store, IReadOnlyList<OutboxEntry> Batch)[] claims =
                await Task.WhenAll(
                    ClaimAsync("instance-A", optionsA, clock, Batch),
                    ClaimAsync("instance-B", optionsB, clock, Batch));

            (OutboxDbContext contextA, EfCoreOutboxStore storeA, IReadOnlyList<OutboxEntry> batchA) = claims[0];
            (OutboxDbContext contextB, EfCoreOutboxStore storeB, IReadOnlyList<OutboxEntry> batchB) = claims[1];

            try
            {
                batchA.Select(e => e.Id).Intersect(batchB.Select(e => e.Id)).Should().BeEmpty(
                    "two concurrently claiming instances must claim disjoint batches via FOR UPDATE SKIP LOCKED");

                // (b) cross-release attempts must not touch the other instance's claims — proven purely
                // by reading the rows back from the database, never from ReleaseLockAsync's return value
                // (which is always empty and proves nothing about which rows were actually touched).
                await storeB.ReleaseLockAsync(batchA.Select(e => e.Id).ToList());
                await storeA.ReleaseLockAsync(batchB.Select(e => e.Id).ToList());

                Dictionary<long, int> nackCountById = batchA
                    .Concat(batchB)
                    .ToDictionary(e => e.Id, e => e.NackCount);
                List<long> bothBatchIds = [.. batchA.Select(e => e.Id), .. batchB.Select(e => e.Id)];
                IReadOnlyDictionary<long, OutboxMessage> rowsAfterCrossRelease = await ReadRowsAsync(bothBatchIds);

                foreach (long id in batchA.Select(e => e.Id))
                {
                    OutboxMessage row = rowsAfterCrossRelease[id];
                    row.LockedBy.Should().Be("instance-A",
                        "instance B's cross-release attempt must not touch instance A's claim");
                    row.RetryCount.Should().Be(nackCountById[id],
                        "a rejected cross-release attempt must not change the row's retry count");
                }

                foreach (long id in batchB.Select(e => e.Id))
                {
                    OutboxMessage row = rowsAfterCrossRelease[id];
                    row.LockedBy.Should().Be("instance-B",
                        "instance A's cross-release attempt must not touch instance B's claim");
                    row.RetryCount.Should().Be(nackCountById[id],
                        "a rejected cross-release attempt must not change the row's retry count");
                }

                // (c) each instance settles only its own batch.
                await SettleAsync(storeA, "instance-A", cycle, batchA, Accept, ledger);
                await SettleAsync(storeB, "instance-B", cycle, batchB, Accept, ledger);
            }
            finally
            {
                ReturnBuffers(batchA);
                ReturnBuffers(batchB);
                await contextA.DisposeAsync();
                await contextB.DisposeAsync();
            }
        }

        (await CountUndeliveredAsync(ids)).Should().Be(0, "every row must eventually be delivered");
        ids.Should().OnlyContain(id => ledger.AckCount(id) == 1, "no row may be delivered twice");

        // Nacked rows carry exactly their scripted retry count, proving the deferral path ran for them.
        IReadOnlyDictionary<long, OutboxMessage> finalRows = await ReadRowsAsync(ids);
        foreach (long id in ids)
        {
            int expectedRetryCount = id % 6 == 0 ? 2 : id % 3 == 0 ? 1 : 0;
            finalRows[id].RetryCount.Should().Be(expectedRetryCount,
                $"row {id} must carry exactly its scripted retry count");
        }

        // Both instances confirmed at least one row — the competition for the shared backlog happened.
        ledger.AnyDeliveredByInstance("instance-A").Should().BeTrue("instance A must have confirmed at least one row");
        ledger.AnyDeliveredByInstance("instance-B").Should().BeTrue("instance B must have confirmed at least one row");
    }

    // ── Test 2: liveness of abandoned claims behind a poison cohort ───────────

    /// <summary>
    /// A dead instance's abandoned claims must be taken over and delivered within a deterministic
    /// number of cycles, even while a permanently rejected "poison" cohort with lower ids keeps
    /// re-entering the retry class ahead of them.
    /// </summary>
    [Fact]
    public async Task GetPendingAsync_AbandonedClaimsBehindPoisonCohort_TakesOverAndDeliversAllWithinBound()
    {
        const int poisonCount = 12; // P — lower ids, rejected on every attempt.
        const int abandonedCount = 10; // K — claimed by an instance that died.
        TimeSpan lockTimeout = TimeSpan.FromSeconds(5);
        OutboxOptions options = CreateOptions(lockTimeout);
        var clock = new ManualClock(DateTimeOffset.UtcNow);

        IReadOnlyList<long> poison = await SeedRowsAsync("p0-poison", poisonCount, clock);
        await MakeDueRetriesAsync(poison, options, clock);

        DateTimeOffset claimMoment = clock.GetUtcNow();
        IReadOnlyList<long> abandoned = await SeedAbandonedClaimsAsync(
            "p0-abandoned", abandonedCount, deadInstanceId: "dead-instance", lockedAt: claimMoment);

        int lockTimeoutCycles = (int)(lockTimeout / options.PollingInterval); // 5
        // Poison rows rejected again in earlier cycles (LockedAt at or shortly after claimMoment) can
        // also queue ahead of the abandoned claims once those become due. By the cycle the abandoned
        // claims first become claimable, at most the whole poison cohort (P rows, each counted once)
        // precedes them in the retry-ordering queue, so the bound below covers the worst case.
        int bound = lockTimeoutCycles + (int)Math.Ceiling((poisonCount + abandonedCount) / (double)RetryShare); // 16

        var ledger = new DeliveryLedger();
        var poisonSet = poison.ToHashSet();
        for (int cycle = 1; cycle <= 2 * bound && await CountUndeliveredAsync(abandoned) > 0; cycle++)
        {
            clock.Advance(options.PollingInterval);
            await SeedRowsAsync($"p0-flood-{cycle}", NewShare, clock); // Keeps R_eff == RetryShare.
            CycleResult result = await RunCycleAsync("live-instance", options, clock, cycle,
                e => !poisonSet.Contains(e.Id), ledger);

            // Sensitivity: an abandoned claim must not be taken over before its lock timed out.
            if (cycle <= lockTimeoutCycles)
            {
                result.ClaimedIds.Intersect(abandoned).Should().BeEmpty(
                    "an abandoned claim must not be taken over before OutboxLockTimeout elapsed");
            }
        }

        abandoned.Should().OnlyContain(id => ledger.DeliveredInCycle(id) <= bound, $"bound = {bound} cycles");
        abandoned.Should().OnlyContain(id => ledger.AckCount(id) == 1);

        // Taking over an abandoned claim is not a nack: the abandoned rows' retry count must be
        // untouched, and every one of them must have ultimately been delivered.
        IReadOnlyDictionary<long, OutboxMessage> abandonedRows = await ReadRowsAsync(abandoned);
        foreach (long id in abandoned)
        {
            OutboxMessage row = abandonedRows[id];
            row.RetryCount.Should().Be(0, "taking over an abandoned claim must not increment its retry count");
            row.DeliveredAt.Should().NotBeNull("every abandoned row must eventually be delivered");
        }

        // The poison cohort stayed active throughout the run: still undelivered, rejected again.
        (await CountUndeliveredAsync(poison)).Should().Be(poisonCount,
            "the poison cohort must stay undelivered — it is rejected on every attempt");

        IReadOnlyDictionary<long, OutboxMessage> poisonRows = await ReadRowsAsync(poison);
        poisonRows.Values.Should().OnlyContain(row => row.RetryCount >= 2,
            "the poison cohort must have been rejected again multiple times across the run");
    }

    // ── Test 3: poison cohort does not starve new rows ────────────────────────

    /// <summary>
    /// A large, permanently rejected "poison" cohort with older ids must not prevent freshly seeded
    /// rows from being delivered within a deterministic number of cycles — the retry reserve is a
    /// floor on the retry class's progress, not a ceiling that could grow and crowd out new rows.
    /// </summary>
    [Fact]
    public async Task GetPendingAsync_PoisonCohortOlderThanNewRows_DeliversEveryNewRowWithinBound()
    {
        const int poisonCount = 40; // Older, lower ids, rejected on every attempt.
        const int freshCount = 30;
        OutboxOptions options = CreateOptions(TimeSpan.FromSeconds(30));
        var clock = new ManualClock(DateTimeOffset.UtcNow);

        IReadOnlyList<long> poison = await SeedRowsAsync("p0-starve-poison", poisonCount, clock);
        await MakeDueRetriesAsync(poison, options, clock);
        IReadOnlyList<long> fresh = await SeedRowsAsync("p0-starve-fresh", freshCount, clock);

        int bound = (int)Math.Ceiling(freshCount / (double)NewShare); // 5
        var ledger = new DeliveryLedger();
        var poisonSet = poison.ToHashSet();
        for (int cycle = 1; cycle <= bound; cycle++)
        {
            clock.Advance(options.PollingInterval);
            CycleResult result = await RunCycleAsync("live-instance", options, clock, cycle,
                e => !poisonSet.Contains(e.Id), ledger);

            // Both classes progress every cycle: the retry reserve is always served, and it never grows
            // while new rows are waiting.
            result.ClaimedIds.Count(poisonSet.Contains).Should().Be(RetryShare,
                "the retry reserve must be served every cycle — it is a floor, not a ceiling");
            result.ClaimedIds.Count.Should().Be(Batch);
        }

        fresh.Should().OnlyContain(id => ledger.DeliveredInCycle(id) <= bound, $"bound = {bound} cycles");
        fresh.Should().OnlyContain(id => ledger.AckCount(id) == 1);
    }

    // ── Test 4: one transient rejection behind a poison cohort ────────────────

    /// <summary>
    /// A row rejected by the transport exactly once (a transient failure) must still be redelivered
    /// despite a permanently rejected ("poison") cohort with lower ids competing for the same
    /// retry-class capacity every cycle.
    /// </summary>
    [Fact]
    public async Task GetPendingAsync_TransientRejectionBehindPoisonCohort_RedeliversEveryRowWithinBound()
    {
        const int poisonCount = 12; // P — lower ids, rejected on every attempt.
        const int transientCount = 4; // K — higher ids, rejected on exactly their first attempt.
        OutboxOptions options = CreateOptions(TimeSpan.FromSeconds(30));
        var clock = new ManualClock(DateTimeOffset.UtcNow);

        IReadOnlyList<long> poison = await SeedRowsAsync("p0-transient-poison", poisonCount, clock);
        await MakeDueRetriesAsync(poison, options, clock);
        IReadOnlyList<long> transient = await SeedRowsAsync("p0-transient-fresh", transientCount, clock);

        // A row's first-ever nack always uses the deferral schedule's bucket-0 deferral, whose minimum
        // is exactly PollingInterval (retryCount == 0, zero jitter) — see OutboxNackDeferralSchedule.
        // The claim predicate compares LockedAt with a strict "<", so even that minimum deferral can
        // never have already elapsed after exactly one clock tick; two ticks are always required,
        // regardless of jitter. Never derived from production code — this is the schedule's documented
        // contract, hard-coded here as this scenario's "claim latency" term.
        const int firstDeferralCycles = 2;

        // R_eff, the retry class's effective capacity per cycle: the poison cohort has already been
        // pushed into the due-retry class before this loop starts (MakeDueRetriesAsync above), so from
        // cycle 1 there is no new-row backlog left to compete with it — every transient row is claimed
        // as "new" well before the retry class would ever need more than its RetryShare reserve, and
        // once poison and transient rows share the retry class, the effective capacity is never less
        // than the RetryShare reserve (it can only be more, when the new-rows step leaves capacity
        // unclaimed). RetryShare is therefore a safe, worst-case-only floor for R_eff.
        int bound = firstDeferralCycles + (int)Math.Ceiling((poisonCount + transientCount) / (double)RetryShare); // 10

        var ledger = new DeliveryLedger();
        var poisonSet = poison.ToHashSet();
        var transientSet = transient.ToHashSet();
        var firstNackCycle = new Dictionary<long, int>();

        bool Accept(OutboxEntry e) => !poisonSet.Contains(e.Id) && e.NackCount >= 1;

        for (int cycle = 1; cycle <= 2 * bound && await CountUndeliveredAsync(transient) > 0; cycle++)
        {
            clock.Advance(options.PollingInterval);
            CycleResult result = await RunCycleAsync("live-instance", options, clock, cycle, Accept, ledger);

            foreach (long id in result.NackedIds.Where(transientSet.Contains))
            {
                firstNackCycle.TryAdd(id, cycle);
            }

            // Sensitivity: a transient row cannot be redelivered in the cycle immediately after its
            // first nack — see firstDeferralCycles above.
            foreach (long id in result.AckedIds.Where(transientSet.Contains))
            {
                firstNackCycle.Should().ContainKey(id, "a transient row must be nacked once before it can be delivered");
                (cycle - firstNackCycle[id]).Should().BeGreaterThanOrEqualTo(firstDeferralCycles,
                    "a transient row cannot be redelivered before its first-nack deferral has elapsed");
            }
        }

        (await CountUndeliveredAsync(transient)).Should().Be(0, "every transiently rejected row must eventually be delivered");
        transient.Should().OnlyContain(id => ledger.DeliveredInCycle(id) <= bound, $"bound = {bound} cycles");
        transient.Should().OnlyContain(id => ledger.AckCount(id) == 1, "no transiently rejected row may be delivered twice");

        IReadOnlyDictionary<long, OutboxMessage> transientRows = await ReadRowsAsync(transient);
        transientRows.Values.Should().OnlyContain(row => row.RetryCount == 1,
            "each transiently rejected row must carry exactly its one scripted nack");

        // The poison cohort stayed active and kept being retried throughout the run.
        (await CountUndeliveredAsync(poison)).Should().Be(poisonCount,
            "the poison cohort must stay undelivered — it is rejected on every attempt");

        IReadOnlyDictionary<long, OutboxMessage> poisonRows = await ReadRowsAsync(poison);
        poisonRows.Values.Should().OnlyContain(row => row.RetryCount >= 2,
            "the poison cohort must have been rejected again multiple times across the run");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private OutboxDbContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<OutboxDbContext>();
        optionsBuilder.UseNpgsql(_connectionString!);
        return new OutboxDbContext(optionsBuilder.Options);
    }

    private static OutboxOptions CreateOptions(TimeSpan lockTimeout)
    {
        var options = new OutboxOptions
        {
            PollingInterval = TimeSpan.FromSeconds(1),
            OutboxLockTimeout = lockTimeout,
            OutboxRetention = TimeSpan.FromDays(7),
            InboxRetention = TimeSpan.FromDays(8),
            InboxLockTimeout = TimeSpan.FromSeconds(30),
            OrderingMode = OrderingMode.None
        };

        // Never measure a liveness bound against a configuration production would reject.
        options.Validate();
        return options;
    }

    // Seeds count fresh rows and returns their ids in ascending order, read from the entities EF Core
    // populated after SaveChangesAsync rather than re-queried — a marker-prefix re-query would risk a
    // false match against a later marker sharing the same prefix (e.g. "p0-flood-1" vs "p0-flood-10").
    private async Task<IReadOnlyList<long>> SeedRowsAsync(string marker, int count, ManualClock clock)
    {
        await using OutboxDbContext context = CreateDbContext();
        DateTimeOffset now = clock.GetUtcNow();
        var rows = new List<OutboxMessage>(count);
        for (int i = 0; i < count; i++)
        {
            var row = new OutboxMessage
            {
                MessageId = Guid.NewGuid(),
                DestinationAddress = $"{marker}.{i}",
                ContentType = "application/json",
                Payload = [1, 2, 3],
                CreatedAt = now
            };
            rows.Add(row);
            context.OutboxMessages.Add(row);
        }

        await context.SaveChangesAsync();

        return rows.Select(r => r.Id).OrderBy(id => id).ToList();
    }

    // Seeds rows already claimed by a dead instance, simulating an abandoned claim.
    private async Task<IReadOnlyList<long>> SeedAbandonedClaimsAsync(
        string marker, int count, string deadInstanceId, DateTimeOffset lockedAt)
    {
        await using OutboxDbContext context = CreateDbContext();
        var rows = new List<OutboxMessage>(count);
        for (int i = 0; i < count; i++)
        {
            var row = new OutboxMessage
            {
                MessageId = Guid.NewGuid(),
                DestinationAddress = $"{marker}.{i}",
                ContentType = "application/json",
                Payload = [1, 2, 3],
                CreatedAt = lockedAt,
                LockedAt = lockedAt,
                LockedBy = deadInstanceId,
                RetryCount = 0
            };
            rows.Add(row);
            context.OutboxMessages.Add(row);
        }

        await context.SaveChangesAsync();

        return rows.Select(r => r.Id).OrderBy(id => id).ToList();
    }

    // Makes the given rows due for retry: a throwaway "setup-instance" claims them all, nacks them
    // (deferring instead of unlocking), then the clock advances past the first-nack deferral so the
    // rows are eligible again on the next real claim.
    private async Task MakeDueRetriesAsync(IReadOnlyList<long> ids, OutboxOptions options, ManualClock clock)
    {
        await using OutboxDbContext context = CreateDbContext();
        var store = new EfCoreOutboxStore(
            context,
            new OutboxInstanceId("setup-instance"),
            new PostgresOutboxSqlDialect(),
            options,
            clock,
            FixedJitterSource.Instance);

        IReadOnlyList<OutboxEntry> claimed = await store.GetPendingAsync(ids.Count);
        try
        {
            claimed.Select(e => e.Id).ToHashSet().Should().BeEquivalentTo(ids.ToHashSet(),
                "the setup claim must own exactly the seeded rows before nacking them");

            await store.ReleaseLockAsync(claimed.Select(e => e.Id).ToList());
        }
        finally
        {
            ReturnBuffers(claimed);
        }

        clock.Advance(options.PollingInterval * 2); // First-nack deferral is <= ~1.2 x PollingInterval.
    }

    // Claims a batch on a fresh context and store. The caller owns the returned context and batch
    // buffers and must release both; on failure before returning, this method releases the context
    // itself so a thrown exception never leaks it.
    private async Task<(OutboxDbContext Context, EfCoreOutboxStore Store, IReadOnlyList<OutboxEntry> Batch)> ClaimAsync(
        string instanceId, OutboxOptions options, ManualClock clock, int batchSize)
    {
        OutboxDbContext context = CreateDbContext();
        try
        {
            var store = new EfCoreOutboxStore(
                context,
                new OutboxInstanceId(instanceId),
                new PostgresOutboxSqlDialect(),
                options,
                clock,
                FixedJitterSource.Instance);

            IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(batchSize);
            return (context, store, batch);
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    // Splits a claimed batch into acked and nacked rows using the scripted transport, settles both
    // (mark delivered / release), and records every confirmed send in the ledger. Never returns the
    // batch's pooled buffers — the caller (which also owns the claim) is responsible for that.
    private static async Task<CycleResult> SettleAsync(
        EfCoreOutboxStore store,
        string instanceId,
        int cycle,
        IReadOnlyList<OutboxEntry> batch,
        Func<OutboxEntry, bool> accept,
        DeliveryLedger ledger)
    {
        var ackedIds = new List<long>();
        var nackedIds = new List<long>();

        foreach (OutboxEntry entry in batch)
        {
            if (accept(entry))
            {
                ackedIds.Add(entry.Id);
            }
            else
            {
                nackedIds.Add(entry.Id);
            }
        }

        if (ackedIds.Count > 0)
        {
            await store.MarkDeliveredAsync(ackedIds);
            foreach (long id in ackedIds)
            {
                ledger.RecordAck(id, instanceId, cycle);
            }
        }

        if (nackedIds.Count > 0)
        {
            await store.ReleaseLockAsync(nackedIds);
        }

        return new CycleResult(cycle, batch.Select(e => e.Id).ToList(), ackedIds, nackedIds);
    }

    // One instance dispatch cycle without a transport: claim, script accept/reject, settle, and always
    // release the claimed buffers and dispose the cycle's context — including when an assertion inside
    // SettleAsync's caller throws.
    private async Task<CycleResult> RunCycleAsync(
        string instanceId,
        OutboxOptions options,
        ManualClock clock,
        int cycle,
        Func<OutboxEntry, bool> accept,
        DeliveryLedger ledger)
    {
        (OutboxDbContext context, EfCoreOutboxStore store, IReadOnlyList<OutboxEntry> batch) =
            await ClaimAsync(instanceId, options, clock, Batch);
        try
        {
            return await SettleAsync(store, instanceId, cycle, batch, accept, ledger);
        }
        finally
        {
            ReturnBuffers(batch);
            await context.DisposeAsync();
        }
    }

    // Reads every row for the given ids in one round trip, from the database rather than the change
    // tracker (AsNoTracking, fresh context), so the result reflects what was actually persisted.
    private async Task<IReadOnlyDictionary<long, OutboxMessage>> ReadRowsAsync(IReadOnlyCollection<long> ids)
    {
        await using OutboxDbContext context = CreateDbContext();
        List<OutboxMessage> rows = await context.OutboxMessages
            .AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .ToListAsync();
        return rows.ToDictionary(m => m.Id);
    }

    private async Task<int> CountUndeliveredAsync(IReadOnlyCollection<long> ids)
    {
        await using OutboxDbContext context = CreateDbContext();
        return await context.OutboxMessages
            .AsNoTracking()
            .CountAsync(m => ids.Contains(m.Id) && m.DeliveredAt == null);
    }

    private static void ReturnBuffers(IReadOnlyList<OutboxEntry> entries)
    {
        foreach (OutboxEntry entry in entries)
        {
            ArrayPool<byte>.Shared.Return(entry.PooledBody);
        }
    }

    // One instance dispatch cycle's outcome: everything it claimed, and how it was split into acked and
    // nacked ids.
    private sealed record CycleResult(int Cycle, IReadOnlyList<long> ClaimedIds, IReadOnlyList<long> AckedIds, IReadOnlyList<long> NackedIds);

    // Records every confirmed send: id -> ordered list of (instanceId, cycle). Exactly-once delivery
    // means exactly one entry per id. Never accessed concurrently — every test settles batches
    // sequentially even when claiming them concurrently.
    private sealed class DeliveryLedger
    {
        private readonly Dictionary<long, List<(string InstanceId, int Cycle)>> _acks = [];

        public void RecordAck(long id, string instanceId, int cycle)
        {
            if (!_acks.TryGetValue(id, out List<(string InstanceId, int Cycle)>? entries))
            {
                entries = [];
                _acks[id] = entries;
            }

            entries.Add((instanceId, cycle));
        }

        public int AckCount(long id) => _acks.TryGetValue(id, out List<(string InstanceId, int Cycle)>? entries) ? entries.Count : 0;

        public int? DeliveredInCycle(long id)
            => _acks.TryGetValue(id, out List<(string InstanceId, int Cycle)>? entries) && entries.Count > 0
                ? entries[0].Cycle
                : null;

        public bool AnyDeliveredByInstance(string instanceId)
            => _acks.Values.Any(entries => entries.Any(entry => entry.InstanceId == instanceId));
    }

    // Manually advanced clock shared by every store instance of one test. Thread-safe because the
    // two-instance scenario claims from two stores concurrently, and both read the clock at once.
    private sealed class ManualClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _utcNow;

        public ManualClock(DateTimeOffset start)
        {
            // Aligned to a whole second: PostgreSQL timestamptz truncates to microseconds, so aligning
            // removes any dependency of the scenarios' sharp-inequality cutoffs on sub-second precision.
            _utcNow = new DateTimeOffset(
                start.Year, start.Month, start.Day, start.Hour, start.Minute, start.Second, start.Offset);
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        public void Advance(TimeSpan delta)
        {
            lock (_gate)
            {
                _utcNow += delta;
            }
        }
    }

    // Deterministic jitter source used by every store in these tests instead of the production default
    // (Random.Shared): a random draw would make cycle-based liveness bounds non-reproducible. Zero is
    // also the tightest bound for the abandoned-claims scenario — the poison cohort's re-rejected rows
    // return at the earliest possible instant and, on a tie, lower ids win, which is the worst case for
    // the abandoned claims' delivery bound.
    private sealed class FixedJitterSource : IOutboxJitterSource
    {
        public static readonly FixedJitterSource Instance = new();

        public double NextDouble() => 0.0;
    }
}
