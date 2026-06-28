using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Outbox;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.UnitTests.Outbox;

// OutboxDialectMismatchChecker is the PerKey ordering startup guard: when the active dialect does not
// DECLARE SupportsPerKeyHeadOfLineOrdering, per-key ordering would silently degrade to passthrough. By
// default the guard FAILS FAST (throws); AllowDegradedOrdering downgrades it to a warning; dialects that
// declare the capability and the None ordering mode are silent. Capability is an explicit contract, never
// inferred from SQL text shape — a dialect whose SQL merely differs textually does NOT pass.
//
// The guard is PROVIDER-AWARE (codex round 8): the capability check applies only when the active EF Core
// provider matches the dialect (the dialect is the runtime claim path). When another provider is active the
// store's client-side fallback claim enforces head-of-line ordering itself, so the guard stands down rather
// than reject a valid single-instance deployment. The unit tests below pin the active provider with a real
// in-memory SQLite scope factory (mirroring OutboxProviderAtomicityCheckerTests) and vary the dialect's
// ProviderName to land on either the dialect path (match) or the fallback path (mismatch).

public sealed class OutboxDialectMismatchWarningTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal logger that captures emitted log events for assertion.
    /// </summary>
    private sealed class FakeLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Events.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// A dialect that does NOT override the 5-arg GetClaimSql — falls back to the DIM passthrough
    /// (delegates to the 4-arg overload, so both modes produce identical SQL).
    /// </summary>
    private sealed class PassthroughDialect(string providerName) : IOutboxSqlDialect
    {
        public string ProviderName => providerName;

        public FormattableString GetClaimSql(
            string instanceId, DateTimeOffset now, DateTimeOffset staleCutoff, int batchSize)
            => $"SELECT 1"; // Shape is irrelevant; the 5-arg passthrough will return this for both modes.
    }

    /// <summary>
    /// A dialect that DOES override the 5-arg GetClaimSql to return textually-DIFFERENT SQL for PerKey,
    /// but the SQL carries NO head-of-line predicate and the dialect does NOT declare
    /// <see cref="IOutboxSqlDialect.SupportsPerKeyHeadOfLineOrdering"/> (it stays at the DIM default of
    /// false). This is the case the old SQL-string-diff guard was fooled by: the text differs, so the diff
    /// concluded "supported" while ordering was still passthrough. The capability-flag guard is not fooled.
    /// </summary>
    private sealed class CosmeticOrderingDialect(string providerName) : IOutboxSqlDialect
    {
        public string ProviderName => providerName;

        public FormattableString GetClaimSql(
            string instanceId, DateTimeOffset now, DateTimeOffset staleCutoff, int batchSize)
            => $"SELECT 1";

        public FormattableString GetClaimSql(
            string instanceId, DateTimeOffset now, DateTimeOffset staleCutoff, int batchSize, OrderingMode orderingMode)
        {
            // Separate return statements so each interpolated string target-types to FormattableString
            // individually (a ternary would collapse both holeless branches to string first).
            if (orderingMode == OrderingMode.PerKey)
            {
                return $"SELECT 2 /* cosmetic difference, NOT a real head-of-line predicate */";
            }

            return $"SELECT 1";
        }
    }

    /// <summary>
    /// A dialect that DECLARES <see cref="IOutboxSqlDialect.SupportsPerKeyHeadOfLineOrdering"/> = true but
    /// does NOT override the 5-arg GetClaimSql — so the DIM passthrough runs and PerKey claim SQL is
    /// identical to None (no head-of-line predicate). The capability flag alone would be fooled by this
    /// half-implementation; the consistency check (PerKey SQL must differ from None when the flag is set)
    /// catches it, because identical claim SQL provably cannot filter head-of-line.
    /// </summary>
    private sealed class DeclaredButPassthroughDialect(string providerName) : IOutboxSqlDialect
    {
        public string ProviderName => providerName;

        public bool SupportsPerKeyHeadOfLineOrdering => true; // claims support...

        // ...but does NOT override the 5-arg overload → DIM delegates to this 4-arg for BOTH modes →
        // identical claim SQL → no head-of-line filtering despite the declaration.
        public FormattableString GetClaimSql(
            string instanceId, DateTimeOffset now, DateTimeOffset staleCutoff, int batchSize)
            => $"SELECT 1";
    }

    /// <summary>
    /// A genuinely supported dialect: DECLARES the capability AND overrides the 5-arg GetClaimSql to return
    /// PerKey claim SQL that differs from None. Passes both the capability gate and the consistency check.
    /// </summary>
    private sealed class SupportedDialect(string providerName) : IOutboxSqlDialect
    {
        public string ProviderName => providerName;

        public bool SupportsPerKeyHeadOfLineOrdering => true;

        public FormattableString GetClaimSql(
            string instanceId, DateTimeOffset now, DateTimeOffset staleCutoff, int batchSize)
            => $"SELECT 1";

        public FormattableString GetClaimSql(
            string instanceId, DateTimeOffset now, DateTimeOffset staleCutoff, int batchSize, OrderingMode orderingMode)
        {
            if (orderingMode == OrderingMode.PerKey)
            {
                return $"SELECT 2"; // genuinely different from None — stands in for a real head-of-line claim
            }

            return $"SELECT 1";
        }
    }

    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// Builds a real <see cref="IServiceScopeFactory"/> resolving an <see cref="OutboxDbContext"/> on the
    /// in-memory SQLite provider, so <c>Database.ProviderName</c> is <see cref="SqliteProviderName"/>. A
    /// dialect whose ProviderName equals that lands on the dialect path; any other lands on the fallback
    /// path. Mirrors OutboxProviderAtomicityCheckerTests — exercises the runtime resolution without an IHost.
    /// </summary>
    private static IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContext<OutboxDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        ServiceProvider sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IServiceScopeFactory>();
    }

    private static OutboxDialectMismatchChecker CreateChecker(
        IOutboxSqlDialect dialect,
        OutboxOptions options,
        ILogger<OutboxDialectMismatchChecker> logger,
        IServiceScopeFactory? scopeFactory = null)
        => new(scopeFactory ?? CreateScopeFactory(), dialect, options, logger);

    private static OutboxOptions PerKeyOptions(bool allowDegradedOrdering = false)
        => new()
        {
            OrderingMode = OrderingMode.PerKey,
            OrderingKeyHeaderName = "correlation-id",
            AllowDegradedOrdering = allowDegradedOrdering,
        };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DialectMismatchChecker_PerKey_UnsupportedDialect_FailsFast()
    {
        // Arrange — passthrough dialect ON the active provider (dialect path) under PerKey + strict opt-out.
        var dialect = new PassthroughDialect(SqliteProviderName);
        var logger = new FakeLogger<OutboxDialectMismatchChecker>();
        var checker = CreateChecker(dialect, PerKeyOptions(), logger);

        // Act + Assert — PerKey on a dialect without head-of-line ordering must fail fast at startup,
        // not silently degrade and deliver out of order.
        await Invoking(() => checker.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<BareWireConfigurationException>(
                "PerKey on a dialect without native head-of-line ordering must fail fast")
            .Where(e => e.Message.Contains(SqliteProviderName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DialectMismatchChecker_PerKey_TextuallyDifferentButUndeclaredDialect_FailsFast()
    {
        // Regression (codex round 6): a dialect whose PerKey SQL differs TEXTUALLY from None but lacks a
        // head-of-line predicate — and does NOT declare SupportsPerKeyHeadOfLineOrdering — must fail fast.
        // The previous guard inferred capability from a SQL-string diff and WRONGLY passed this dialect,
        // leaving operators believing PerKey was protected while messages sharing a key could still be
        // delivered out of order. Capability is now an explicit declaration, never inferred from SQL shape.
        var dialect = new CosmeticOrderingDialect(SqliteProviderName);
        var logger = new FakeLogger<OutboxDialectMismatchChecker>();
        var checker = CreateChecker(dialect, PerKeyOptions(), logger);

        await Invoking(() => checker.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<BareWireConfigurationException>(
                "a dialect that did not declare PerKey support must fail fast even when its SQL differs textually")
            .Where(e => e.Message.Contains(SqliteProviderName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DialectMismatchChecker_PerKey_DeclaredButPassthroughSql_FailsFast()
    {
        // Regression (codex stop-time review): a dialect can SET the capability flag yet still ship
        // passthrough SQL (e.g. it declared the flag but never overrode the 5-arg GetClaimSql). Identical
        // PerKey/None claim SQL provably cannot enforce head-of-line ordering, so the flag-plus-diff
        // consistency check must reject it — the explicit declaration is not a bypass.
        var dialect = new DeclaredButPassthroughDialect(SqliteProviderName);
        var logger = new FakeLogger<OutboxDialectMismatchChecker>();
        var checker = CreateChecker(dialect, PerKeyOptions(), logger);

        await Invoking(() => checker.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<BareWireConfigurationException>(
                "declaring the capability while the claim SQL stays passthrough must still fail fast")
            .Where(e => e.Message.Contains(SqliteProviderName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DialectMismatchChecker_PerKey_DeclaredButPassthroughSql_AllowDegradedOrdering_Warns()
    {
        // Same half-implementation, but the caller explicitly opted into degraded ordering — warn, not throw.
        var dialect = new DeclaredButPassthroughDialect(SqliteProviderName);
        var logger = new FakeLogger<OutboxDialectMismatchChecker>();
        var checker = CreateChecker(dialect, PerKeyOptions(allowDegradedOrdering: true), logger);

        await Invoking(() => checker.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync(
                "AllowDegradedOrdering=true downgrades even the declared-but-passthrough case to a warning");

        logger.Events.Should().ContainSingle(e => e.Level == LogLevel.Warning,
            "the declared-but-passthrough opt-out path must emit exactly one mismatch warning");
    }

    [Fact]
    public async Task DialectMismatchChecker_PerKey_UnsupportedDialect_AllowDegradedOrdering_Warns()
    {
        // Arrange — same mismatch, but the caller has explicitly opted into degraded ordering.
        var dialect = new PassthroughDialect(SqliteProviderName);
        var logger = new FakeLogger<OutboxDialectMismatchChecker>();
        var checker = CreateChecker(dialect, PerKeyOptions(allowDegradedOrdering: true), logger);

        // Act — must NOT throw; instead emits exactly one warning.
        await Invoking(() => checker.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync("AllowDegradedOrdering=true downgrades the fail-fast guard to a warning");

        logger.Events.Should().ContainSingle(e => e.Level == LogLevel.Warning,
            "the opt-out path must emit exactly one mismatch warning");
    }

    [Fact]
    public async Task DialectMismatchChecker_PerKey_SupportedDialect_NoWarningNoThrow()
    {
        // Arrange — a dialect that declares SupportsPerKeyHeadOfLineOrdering = true AND overrides the 5-arg
        // GetClaimSql with PerKey SQL that differs from None — native ordering support on the active provider.
        var dialect = new SupportedDialect(SqliteProviderName);
        var logger = new FakeLogger<OutboxDialectMismatchChecker>();
        var checker = CreateChecker(dialect, PerKeyOptions(), logger);

        // Act + Assert — no throw and no warning.
        await Invoking(() => checker.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync("a dialect with native per-key ordering must pass the guard");

        logger.Events.Should().NotContain(e => e.Level == LogLevel.Warning,
            "a dialect that declares the capability and overrides GetClaimSql(5-arg) — no mismatch warning expected");
    }

    [Fact]
    public async Task DialectMismatchChecker_PerKey_ProviderUsesFallback_DoesNotThrow()
    {
        // Regression (codex round 8): when the active EF provider has NO matching dialect, the store uses
        // the client-side fallback claim, which itself enforces PerKey head-of-line ordering (the head-of-
        // line filter in EfCoreOutboxStore.GetPendingAsync). The guard must NOT reject such a deployment
        // based on the unused dialect's capability. Active provider here is SQLite; the dialect targets
        // Npgsql (≠ active) and is non-capable — pre-fix this threw (a false rejection of a valid single-
        // instance deployment); now it must start cleanly. Paired with the matching-provider fail-fast tests
        // above, this proves the guard keys on provider match, not capability alone (anti-tautology).
        var dialect = new PassthroughDialect(NpgsqlProviderName);
        var logger = new FakeLogger<OutboxDialectMismatchChecker>();
        var checker = CreateChecker(dialect, PerKeyOptions(), logger); // strict (AllowDegradedOrdering=false)

        await Invoking(() => checker.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync(
                "the client-side fallback enforces PerKey ordering when no dialect matches the active provider");

        logger.Events.Should().NotContain(e => e.Level == LogLevel.Warning,
            "the fallback path is not a degraded-ordering warning — ordering is enforced, just not by the dialect");

        logger.Events.Should().ContainSingle(e => e.Level == LogLevel.Debug,
            "a single Debug log records that the fallback enforces ordering for the unmatched provider");
    }

    [Fact]
    public async Task DialectMismatchChecker_PerKey_DefaultDialectOnUnmatchedProvider_DoesNotThrow()
    {
        // The shipped default PostgresOutboxSqlDialect targets Npgsql. On a SQLite single-instance
        // deployment (AllowNonAtomicProvider) the store uses the client-side fallback, which enforces PerKey
        // ordering — so PerKey on SQLite with the default dialect must start cleanly, not fail fast. This is
        // the common dev/test scenario; it must not require AllowDegradedOrdering.
        var dialect = new PostgresOutboxSqlDialect();
        var logger = new FakeLogger<OutboxDialectMismatchChecker>();
        var checker = CreateChecker(dialect, PerKeyOptions(), logger);

        await Invoking(() => checker.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync(
                "the default dialect on an unmatched provider falls back to the client-side claim, which enforces ordering");
    }

    [Fact]
    public async Task DialectMismatchChecker_None_NoWarningNoThrow()
    {
        // Arrange — under OrderingMode.None there is no ordering guarantee to enforce, so even a
        // passthrough dialect is fine: the guard must short-circuit without throwing or warning.
        var dialect = new PassthroughDialect(SqliteProviderName);
        var logger = new FakeLogger<OutboxDialectMismatchChecker>();
        var options = new OutboxOptions { OrderingMode = OrderingMode.None };
        var checker = CreateChecker(dialect, options, logger);

        // Act + Assert
        await Invoking(() => checker.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync("the ordering guard must not apply under OrderingMode.None");

        await checker.StopAsync(CancellationToken.None); // must not throw

        logger.Events.Should().NotContain(e => e.Level == LogLevel.Warning,
            "under None the guard must stay silent regardless of dialect");
    }

    [Fact]
    public async Task DialectMismatchChecker_Warning_ContainsNoSensitiveValues()
    {
        // Arrange — passthrough dialect on the active provider + opt-out triggers the warning (not the throw).
        var dialect = new PassthroughDialect(SqliteProviderName);
        var logger = new FakeLogger<OutboxDialectMismatchChecker>();
        var checker = CreateChecker(dialect, PerKeyOptions(allowDegradedOrdering: true), logger);

        // Act
        await checker.StartAsync(CancellationToken.None);

        // Assert — the logged message must contain only the provider name,
        // never any SQL keywords, OrderingKey values, LockedBy values, or other sensitive data.
        logger.Events.Should().ContainSingle(e => e.Level == LogLevel.Warning);

        string warningMessage = logger.Events.Single(e => e.Level == LogLevel.Warning).Message;

        warningMessage.Should().Contain(SqliteProviderName,
            "the warning must identify the offending dialect by its ProviderName");

        warningMessage.Should().NotContainAny(
            ["NOT EXISTS", "FOR UPDATE", "SKIP LOCKED", "LockedBy", "OrderingKey", "__check__"],
            "warning must not leak SQL structure, internal lock values, or sentinel parameters");
    }

    // ── Helper for fluent Func<Task> assertions ───────────────────────────────

    private static Func<Task> Invoking(Func<Task> action) => action;
}
