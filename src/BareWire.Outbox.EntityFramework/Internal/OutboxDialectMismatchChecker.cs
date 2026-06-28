using System.Globalization;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Outbox;
using BareWire.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareWire.Outbox.EntityFramework.Internal;

/// <summary>
/// Startup guard for <see cref="OrderingMode.PerKey"/>. When the registered <see cref="IOutboxSqlDialect"/>
/// does not declare <see cref="IOutboxSqlDialect.SupportsPerKeyHeadOfLineOrdering"/>, per-key head-of-line
/// ordering cannot be enforced and silently degrades to passthrough. By default this fails fast at startup
/// so a misconfigured deployment never delivers out of order; <see cref="OutboxOptions.AllowDegradedOrdering"/>
/// downgrades the failure to a warning for callers that explicitly accept passthrough ordering. Capability
/// is read from the dialect's explicit declaration — never inferred from the shape of its SQL, because a
/// dialect can emit cosmetically different PerKey SQL while still behaving as passthrough. As a consistency
/// check, a dialect that declares the capability but whose PerKey claim SQL is identical to its None claim
/// SQL (a half-implementation that set the flag without a real 5-arg override) is rejected too.
/// The check is provider-aware: it applies only when the active EF Core provider matches the dialect (the
/// dialect is the runtime claim path). When another provider is active the store's client-side fallback
/// claim enforces head-of-line ordering itself, so this guard stands down rather than rejecting a valid
/// single-instance deployment.
/// </summary>
internal sealed partial class OutboxDialectMismatchChecker : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOutboxSqlDialect _dialect;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDialectMismatchChecker> _logger;

    public OutboxDialectMismatchChecker(
        IServiceScopeFactory scopeFactory,
        IOutboxSqlDialect dialect,
        OutboxOptions options,
        ILogger<OutboxDialectMismatchChecker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The ordering guarantee only applies to PerKey; under None there is nothing to check.
        if (_options.OrderingMode != OrderingMode.PerKey)
        {
            return;
        }

        // The store routes the atomic claim to the dialect ONLY when the active EF Core provider matches
        // the dialect's ProviderName (EfCoreOutboxStore.GetPendingAsync); any other provider uses the
        // client-side fallback claim, which ALWAYS enforces PerKey head-of-line ordering regardless of the
        // dialect. So the dialect's capability is only relevant when the dialect is the active claim path.
        // Resolve the active provider the same way the sibling OutboxProviderAtomicityChecker does
        // (ProviderName is static configuration — no DB round-trip / connection needed).
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        OutboxDbContext ctx = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
        string? activeProvider = ctx.Database.ProviderName;

        if (!string.Equals(activeProvider, _dialect.ProviderName, StringComparison.Ordinal))
        {
            // The active provider has no matching dialect, so the store uses the client-side fallback claim
            // (EfCoreOutboxStore.GetPendingAsync), which enforces PerKey head-of-line ordering on its own.
            // The dialect's capability is irrelevant on this path — fail-fast here would falsely reject a
            // valid single-instance deployment. The provider-atomicity guard separately governs whether the
            // non-atomic fallback is permitted at all (AllowNonAtomicProvider).
            LogOrderingEnforcedByFallback(_logger, activeProvider ?? "(null)", _dialect.ProviderName);
            return;
        }

        // PRIMARY gate: capability is an EXPLICIT declaration by the dialect, never inferred from the
        // shape of the returned SQL — a dialect can emit cosmetically different PerKey SQL while still
        // behaving as passthrough, so a SQL-text diff alone would give false confidence.
        //
        // SECONDARY consistency check: a dialect can also DECLARE the capability yet still ship passthrough
        // SQL (it set the flag but never overrode the 5-arg GetClaimSql). Identical PerKey/None claim SQL
        // provably cannot filter head-of-line — the claim is what selects rows, so identical SQL selects
        // identical rows — therefore a declaration paired with identical SQL is a half-implementation and is
        // rejected too. A genuinely supported dialect declares the flag AND its PerKey claim SQL differs.
        if (_dialect.SupportsPerKeyHeadOfLineOrdering && PerKeyClaimSqlDiffersFromNone())
        {
            return;
        }

        // Either the dialect does not declare the capability, or it declares it but its claim SQL is
        // passthrough. Fail fast by default so a misconfigured deployment never delivers out of order;
        // AllowDegradedOrdering downgrades this to a warning for callers that knowingly accept passthrough.
        if (_options.AllowDegradedOrdering)
        {
            LogDialectDoesNotSupportOrdering(_logger, _dialect.ProviderName);
            return;
        }

        string reason = _dialect.SupportsPerKeyHeadOfLineOrdering
            ? "declares SupportsPerKeyHeadOfLineOrdering=true but its PerKey claim SQL is identical to its " +
              "None claim SQL (passthrough — the 5-arg GetClaimSql override is missing or emits no " +
              "head-of-line predicate)"
            : "does not declare SupportsPerKeyHeadOfLineOrdering";

        throw new BareWireConfigurationException(
            $"OutboxOptions.OrderingMode is PerKey but the registered IOutboxSqlDialect " +
            $"(provider='{_dialect.ProviderName}') {reason}, so per-key head-of-line ordering would " +
            $"silently degrade to passthrough — messages sharing an ordering key could be delivered out of " +
            $"order and then marked delivered (irreversible). To fix: (a) register an IOutboxSqlDialect " +
            $"that overrides GetClaimSql(string, DateTimeOffset, DateTimeOffset, int, OrderingMode) with a " +
            $"head-of-line predicate AND returns true from SupportsPerKeyHeadOfLineOrdering for provider " +
            $"'{_dialect.ProviderName}', (b) set OutboxOptions.OrderingMode to None, or (c) set " +
            $"AllowDegradedOrdering = true to explicitly accept passthrough ordering.");
    }

    /// <summary>
    /// Renders the dialect's claim SQL under <see cref="OrderingMode.PerKey"/> and
    /// <see cref="OrderingMode.None"/> with fixed sentinel parameters and reports whether the two differ.
    /// Used ONLY as a consistency check on a dialect that DECLARES
    /// <see cref="IOutboxSqlDialect.SupportsPerKeyHeadOfLineOrdering"/>: identical claim SQL provably cannot
    /// enforce head-of-line ordering (the claim is what selects rows), so a declared-true dialect with
    /// identical SQL is a half-implementation. This is NOT the capability oracle — the explicit flag is.
    /// </summary>
    private bool PerKeyClaimSqlDiffersFromNone()
    {
        // Fixed, stable sentinel values so the FormattableString shapes compare on SQL *structure* only,
        // independent of the current time. InvariantCulture keeps the comparison locale-independent (SQL
        // templates carry no locale-sensitive content, but CA1305 requires an explicit IFormatProvider).
        const string sentinelInstanceId = "__check__";
        DateTimeOffset sentinelTime = DateTimeOffset.UnixEpoch;
        const int sentinelBatch = 1;

        FormattableString perKeySql = _dialect.GetClaimSql(
            sentinelInstanceId, sentinelTime, sentinelTime, sentinelBatch, OrderingMode.PerKey);
        FormattableString noneSql = _dialect.GetClaimSql(
            sentinelInstanceId, sentinelTime, sentinelTime, sentinelBatch, OrderingMode.None);

        return !string.Equals(
            perKeySql.ToString(CultureInfo.InvariantCulture),
            noneSql.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "OutboxOptions.OrderingMode is PerKey but the active EF Core provider ('{ActiveProvider}') " +
                  "has no matching IOutboxSqlDialect ('{DialectProvider}'); the store uses the client-side " +
                  "fallback claim, which enforces per-key head-of-line ordering, so the dialect capability " +
                  "check does not apply on this path.")]
    private static partial void LogOrderingEnforcedByFallback(
        ILogger logger,
        string activeProvider,
        string dialectProvider);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "OutboxOptions.OrderingMode is PerKey but the registered IOutboxSqlDialect " +
                  "(provider={ProviderName}) does not provide per-key head-of-line ordering (capability " +
                  "undeclared, or declared but the claim SQL is passthrough) — PerKey will degrade to " +
                  "passthrough with no head-of-line ordering guarantee. Override " +
                  "GetClaimSql(string, DateTimeOffset, DateTimeOffset, int, OrderingMode) with a " +
                  "head-of-line predicate and return true from SupportsPerKeyHeadOfLineOrdering, or " +
                  "switch to a provider with native support.")]
    private static partial void LogDialectDoesNotSupportOrdering(ILogger logger, string providerName);
}
