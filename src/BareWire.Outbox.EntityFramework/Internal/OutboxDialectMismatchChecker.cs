using System.Globalization;
using BareWire.Abstractions.Outbox;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareWire.Outbox.EntityFramework.Internal;

/// <summary>
/// Startup checker that warns when <see cref="OutboxOptions.OrderingMode"/> is
/// <see cref="OrderingMode.PerKey"/> but the registered <see cref="IOutboxSqlDialect"/> does
/// not override the 5-arg <c>GetClaimSql</c> overload (i.e. it delegates to the 4-arg
/// default-interface-method passthrough). In that case PerKey ordering silently degrades to
/// passthrough with no head-of-line guarantee.
/// </summary>
internal sealed partial class OutboxDialectMismatchChecker : IHostedService
{
    private readonly IOutboxSqlDialect _dialect;
    private readonly ILogger<OutboxDialectMismatchChecker> _logger;

    public OutboxDialectMismatchChecker(
        IOutboxSqlDialect dialect,
        ILogger<OutboxDialectMismatchChecker> logger)
    {
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Use fixed, stable sentinel values so the FormattableString shapes compare identically
        // regardless of the current time — only the SQL *structure* matters, not parameter literals.
        const string sentinelInstanceId = "__check__";
        DateTimeOffset sentinelTime = DateTimeOffset.UnixEpoch;
        const int sentinelBatch = 1;

        FormattableString perKeySql = _dialect.GetClaimSql(
            sentinelInstanceId, sentinelTime, sentinelTime, sentinelBatch, OrderingMode.PerKey);

        FormattableString noneSql = _dialect.GetClaimSql(
            sentinelInstanceId, sentinelTime, sentinelTime, sentinelBatch, OrderingMode.None);

        // If both SQL shapes produce identical output the dialect delegates the 5-arg call to the
        // 4-arg DIM passthrough — PerKey ordering will silently degrade to no ordering at all.
        // Use InvariantCulture so the comparison is locale-independent (SQL template strings
        // contain no locale-sensitive content, but CA1305 requires an explicit IFormatProvider).
        if (string.Equals(
                perKeySql.ToString(CultureInfo.InvariantCulture),
                noneSql.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            LogDialectDoesNotSupportOrdering(_logger, _dialect.ProviderName);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "OutboxOptions.OrderingMode is PerKey but the registered IOutboxSqlDialect " +
                  "(provider={ProviderName}) does not override the 5-arg GetClaimSql — PerKey will " +
                  "degrade to passthrough with no head-of-line ordering guarantee. Override " +
                  "GetClaimSql(string, DateTimeOffset, DateTimeOffset, int, OrderingMode) in your " +
                  "dialect or switch to a provider with native support.")]
    private static partial void LogDialectDoesNotSupportOrdering(ILogger logger, string providerName);
}
