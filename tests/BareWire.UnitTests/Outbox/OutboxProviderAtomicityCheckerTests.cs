using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.UnitTests.Outbox;

// B6 — Startup fail-fast guard: OutboxProviderAtomicityChecker throws BareWireConfigurationException
// when the active EF Core provider has no matching atomic outbox/inbox dialect.

public sealed class OutboxProviderAtomicityCheckerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private sealed class StubOutboxSqlDialect(string providerName) : IOutboxSqlDialect
    {
        public string ProviderName => providerName;

        public FormattableString GetClaimSql(
            string instanceId, DateTimeOffset now, DateTimeOffset staleCutoff, int batchSize)
            => $"";
    }

    private sealed class StubInboxSqlDialect(string providerName) : IInboxSqlDialect
    {
        string IInboxSqlDialect.ProviderName => providerName;

        public FormattableString GetUpsertSql(
            Guid messageId, string consumerType, DateTimeOffset receivedAt, DateTimeOffset expiresAt)
            => $"";
    }

    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// Builds a real <see cref="IServiceScopeFactory"/> that resolves an
    /// <see cref="OutboxDbContext"/> configured for the in-memory SQLite provider.
    /// Using a real scope factory mirrors the runtime path without booting a full IHost.
    /// </summary>
    private static IServiceScopeFactory CreateSqliteScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContext<OutboxDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        ServiceProvider sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IServiceScopeFactory>();
    }

    private static OutboxProviderAtomicityChecker CreateChecker(
        IServiceScopeFactory scopeFactory,
        IOutboxSqlDialect outboxDialect,
        IInboxSqlDialect inboxDialect,
        bool allowNonAtomicProvider = false)
    {
        OutboxOptions options = OutboxOptions.Default with { AllowNonAtomicProvider = allowNonAtomicProvider };
        var logger = new FakeLogger<OutboxProviderAtomicityChecker>();
        return new OutboxProviderAtomicityChecker(scopeFactory, outboxDialect, inboxDialect, options, logger);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_WhenProviderHasNoMatchingDialect_ThrowsBareWireConfigurationException()
    {
        // Arrange — SQLite context + both dialects target Npgsql → provider mismatch.
        // RED case: no checker exists yet, so the test cannot compile until the class is created.
        // After the class is created, StartAsync must throw BareWireConfigurationException.
        IServiceScopeFactory scopeFactory = CreateSqliteScopeFactory();
        var outboxDialect = new StubOutboxSqlDialect(NpgsqlProviderName);
        var inboxDialect = new StubInboxSqlDialect(NpgsqlProviderName);
        OutboxProviderAtomicityChecker checker = CreateChecker(scopeFactory, outboxDialect, inboxDialect);

        // Act
        Func<Task> act = () => checker.StartAsync(default);

        // Assert
        await act.Should().ThrowAsync<BareWireConfigurationException>();
    }

    [Fact]
    public async Task StartAsync_WhenAllowNonAtomicProvider_DoesNotThrow()
    {
        // Arrange — same provider mismatch (SQLite + Npgsql dialects) but opt-out flag is set.
        IServiceScopeFactory scopeFactory = CreateSqliteScopeFactory();
        var outboxDialect = new StubOutboxSqlDialect(NpgsqlProviderName);
        var inboxDialect = new StubInboxSqlDialect(NpgsqlProviderName);
        OutboxProviderAtomicityChecker checker = CreateChecker(
            scopeFactory, outboxDialect, inboxDialect, allowNonAtomicProvider: true);

        // Act
        Func<Task> act = () => checker.StartAsync(default);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_WhenProviderMatchesBothDialects_DoesNotThrow()
    {
        // Arrange — SQLite context + both stub dialects targeting SQLite → both match → atomic path OK.
        IServiceScopeFactory scopeFactory = CreateSqliteScopeFactory();
        var outboxDialect = new StubOutboxSqlDialect(SqliteProviderName);
        var inboxDialect = new StubInboxSqlDialect(SqliteProviderName);
        OutboxProviderAtomicityChecker checker = CreateChecker(scopeFactory, outboxDialect, inboxDialect);

        // Act
        Func<Task> act = () => checker.StartAsync(default);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_WhenOnlyOutboxDialectMatches_ThrowsBareWireConfigurationException()
    {
        // Arrange — outbox stub targets SQLite (matches), inbox stub targets Npgsql (mismatch).
        // Anti-tautology: proves that the inbox dialect is also checked, not just the outbox.
        IServiceScopeFactory scopeFactory = CreateSqliteScopeFactory();
        var outboxDialect = new StubOutboxSqlDialect(SqliteProviderName);
        var inboxDialect = new StubInboxSqlDialect(NpgsqlProviderName);
        OutboxProviderAtomicityChecker checker = CreateChecker(scopeFactory, outboxDialect, inboxDialect);

        // Act
        Func<Task> act = () => checker.StartAsync(default);

        // Assert
        await act.Should().ThrowAsync<BareWireConfigurationException>();
    }

    [Fact]
    public async Task StartAsync_WhenOnlyInboxDialectMatches_ThrowsBareWireConfigurationException()
    {
        // Arrange — inbox stub targets SQLite (matches), outbox stub targets Npgsql (mismatch).
        // SEC-2 symmetric anti-tautology: proves that the outbox dialect is also checked, not just the inbox.
        IServiceScopeFactory scopeFactory = CreateSqliteScopeFactory();
        var outboxDialect = new StubOutboxSqlDialect(NpgsqlProviderName);
        var inboxDialect = new StubInboxSqlDialect(SqliteProviderName);
        OutboxProviderAtomicityChecker checker = CreateChecker(scopeFactory, outboxDialect, inboxDialect);

        // Act
        Func<Task> act = () => checker.StartAsync(default);

        // Assert
        await act.Should().ThrowAsync<BareWireConfigurationException>();
    }
}
