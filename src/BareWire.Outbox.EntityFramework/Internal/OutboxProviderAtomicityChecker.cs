using BareWire.Abstractions.Exceptions;
using BareWire.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareWire.Outbox.EntityFramework.Internal;

/// <summary>
/// Startup checker that enforces the atomic-provider requirement at host start.
/// Reads the active EF Core provider name from <c>OutboxDbContext.Database.ProviderName</c>
/// and compares it against the registered <see cref="IOutboxSqlDialect"/> and
/// <see cref="IInboxSqlDialect"/>. When neither dialect matches the active provider the
/// outbox/inbox stores silently fall back to a NON-ATOMIC client-side path that breaks
/// claim/dedup invariants under multi-instance load.
/// </summary>
/// <remarks>
/// Throws <see cref="BareWireConfigurationException"/> on mismatch unless
/// <see cref="OutboxOptions.AllowNonAtomicProvider"/> is <see langword="true"/>, in which
/// case a Warning is logged and startup proceeds (single-instance / testing opt-out).
/// </remarks>
internal sealed partial class OutboxProviderAtomicityChecker : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOutboxSqlDialect _outboxDialect;
    private readonly IInboxSqlDialect _inboxDialect;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxProviderAtomicityChecker> _logger;

    public OutboxProviderAtomicityChecker(
        IServiceScopeFactory scopeFactory,
        IOutboxSqlDialect outboxDialect,
        IInboxSqlDialect inboxDialect,
        OutboxOptions options,
        ILogger<OutboxProviderAtomicityChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(outboxDialect);
        ArgumentNullException.ThrowIfNull(inboxDialect);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _outboxDialect = outboxDialect;
        _inboxDialect = inboxDialect;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // PERF-1: deterministic dispose on both the happy and the throw path.
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        OutboxDbContext ctx = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
        string? active = ctx.Database.ProviderName;

        bool outboxOk = string.Equals(active, _outboxDialect.ProviderName, StringComparison.Ordinal);
        bool inboxOk = string.Equals(active, _inboxDialect.ProviderName, StringComparison.Ordinal);

        if (outboxOk && inboxOk)
        {
            // Both dialects match the active provider — atomic path confirmed.
            return;
        }

        if (_options.AllowNonAtomicProvider)
        {
            // SEC-1: self-describing warning with stable EventId so operators can grep for it.
            LogNonAtomicFallbackActive(
                _logger,
                active ?? "(null)",
                _outboxDialect.ProviderName,
                _inboxDialect.ProviderName);

            return;
        }

        // Build a list of which dialect(s) don't match for the actionable error message.
        var mismatches = new List<string>(2);
        if (!outboxOk)
        {
            mismatches.Add($"IOutboxSqlDialect.ProviderName='{_outboxDialect.ProviderName}'");
        }

        if (!inboxOk)
        {
            mismatches.Add($"IInboxSqlDialect.ProviderName='{_inboxDialect.ProviderName}'");
        }

        string dialectList = string.Join(", ", mismatches);

        throw new BareWireConfigurationException(
            $"The active EF Core provider '{active}' does not match the registered atomic dialect(s): " +
            $"{dialectList}. The outbox/inbox stores would fall back to a NON-ATOMIC client-side " +
            $"fallback that breaks claim/dedup invariants under multi-instance load. " +
            $"To fix: (a) register a custom IOutboxSqlDialect and/or IInboxSqlDialect whose " +
            $"ProviderName equals '{active}' BEFORE calling AddBareWireOutbox, or " +
            $"(b) set AllowNonAtomicProvider = true in the outbox configurator if this is a " +
            $"single-instance deployment or a test environment.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 7601,
        Level = LogLevel.Warning,
        Message = "OutboxProviderAtomicityChecker: active EF Core provider '{ActiveProvider}' does not " +
                  "match IOutboxSqlDialect ('{OutboxDialectProvider}') or IInboxSqlDialect " +
                  "('{InboxDialectProvider}'). AllowNonAtomicProvider=true — NON-ATOMIC client-side " +
                  "fallback is active. This is unsafe for multi-instance deployments and must not be " +
                  "used in production with more than one dispatcher instance.")]
    private static partial void LogNonAtomicFallbackActive(
        ILogger logger,
        string activeProvider,
        string outboxDialectProvider,
        string inboxDialectProvider);
}
