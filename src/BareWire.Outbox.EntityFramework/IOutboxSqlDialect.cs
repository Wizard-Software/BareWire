using BareWire.Abstractions.Outbox;

namespace BareWire.Outbox.EntityFramework;

/// <summary>
/// Provider-specific SQL dialect for outbox row claim operations.
/// </summary>
/// <remarks>
/// Implement this interface to provide an atomic claim SQL statement for a database provider
/// other than PostgreSQL. The outbox store invokes
/// <see cref="GetClaimSql(string, DateTimeOffset, DateTimeOffset, int, OrderingMode)"/> only when
/// the active context's provider matches <see cref="ProviderName"/>; for any other provider it
/// falls back to a non-atomic client-side claim (safe for a single dispatcher instance / testing,
/// not for multi-instance production). The default registration is
/// <see cref="PostgresOutboxSqlDialect"/> (PostgreSQL with <c>FOR UPDATE SKIP LOCKED</c>). Register
/// a custom implementation via
/// <c>services.AddSingleton&lt;IOutboxSqlDialect, MyDialect&gt;()</c> before calling
/// <c>AddBareWireOutbox</c> (it replaces the default), setting <see cref="ProviderName"/> to the
/// provider you target so the store actually invokes it.
/// </remarks>
public interface IOutboxSqlDialect
{
    /// <summary>
    /// The EF Core provider name this dialect targets — compared against
    /// <c>DbContext.Database.ProviderName</c> (e.g. <c>"Npgsql.EntityFrameworkCore.PostgreSQL"</c>,
    /// <c>"Microsoft.EntityFrameworkCore.SqlServer"</c>). The outbox store calls
    /// <see cref="GetClaimSql(string, DateTimeOffset, DateTimeOffset, int, OrderingMode)"/> only
    /// when this value equals the active provider's name; otherwise it uses the client-side
    /// fallback claim.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Indicates whether this dialect's 5-argument
    /// <see cref="GetClaimSql(string, DateTimeOffset, DateTimeOffset, int, OrderingMode)"/> overload emits
    /// a genuine per-key head-of-line ordering predicate under <see cref="OrderingMode.PerKey"/> (for
    /// example a correlated <c>NOT EXISTS</c> that blocks a row while a strictly older undelivered row with
    /// the same ordering key exists).
    /// </summary>
    /// <remarks>
    /// This is an <b>explicit capability declaration</b>, not something the framework can — or does —
    /// infer from the shape of the returned SQL. The <see cref="OrderingMode.PerKey"/> startup guard reads
    /// this flag: a dialect that returns <see langword="false"/> (the default) is rejected at startup when
    /// <see cref="OrderingMode.PerKey"/> is configured, because per-key ordering would otherwise silently
    /// degrade to passthrough and deliver messages sharing a key out of order. To support ordering, override
    /// the 5-arg <c>GetClaimSql</c> with a real head-of-line predicate <b>and</b> return
    /// <see langword="true"/> here. Declaring <see langword="true"/> is not a bypass: the guard additionally
    /// rejects a declared-true dialect whose <see cref="OrderingMode.PerKey"/> claim SQL is identical to its
    /// <see cref="OrderingMode.None"/> claim SQL (a half-implementation that set the flag without a real
    /// override), since identical claim SQL cannot enforce head-of-line ordering. Defaults to
    /// <see langword="false"/> so existing dialects remain passthrough-safe and never accidentally opt in.
    /// </remarks>
    /// <remarks>
    /// <b>Trust boundary.</b> These startup checks catch <i>accidental</i> misconfiguration (an undeclared
    /// capability, or a declared capability backed by passthrough SQL). They <b>cannot</b> verify that a
    /// custom dialect's <see cref="OrderingMode.PerKey"/> SQL actually enforces head-of-line ordering — that
    /// is undecidable from outside the dialect. A dialect that declares <see langword="true"/> and emits
    /// superficially different but semantically incorrect SQL will pass startup and may still deliver
    /// same-key messages out of order. You, the dialect author, are responsible for the correctness of the
    /// head-of-line predicate; for a guaranteed-correct implementation use a framework-provided dialect
    /// (<see cref="PostgresOutboxSqlDialect"/> today).
    /// </remarks>
    bool SupportsPerKeyHeadOfLineOrdering => false;

    /// <summary>
    /// Returns a parameterized SQL statement that atomically claims up to
    /// <paramref name="batchSize"/> unclaimed or stale-locked outbox rows for the given
    /// <paramref name="instanceId"/>. Claimed rows are identified by <c>LockedBy = instanceId</c>
    /// and <c>DeliveredAt IS NULL</c> in the subsequent SELECT.
    /// </summary>
    /// <param name="instanceId">
    /// The unique identifier of the calling dispatcher instance.
    /// Used as the <c>LockedBy</c> value on claimed rows.
    /// </param>
    /// <param name="now">The current UTC time used as the <c>LockedAt</c> value.</param>
    /// <param name="staleCutoff">
    /// Rows with <c>LockedAt &lt; staleCutoff</c> are treated as stale and eligible for
    /// re-claim. Computed as <c>now - OutboxLockTimeout</c>.
    /// </param>
    /// <param name="batchSize">Maximum number of rows to claim in a single call.</param>
    /// <returns>
    /// A <see cref="FormattableString"/> suitable for use with
    /// <see cref="Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlAsync"/>.
    /// All user-supplied values are passed as parameters — never interpolated directly.
    /// </returns>
    FormattableString GetClaimSql(string instanceId, DateTimeOffset now, DateTimeOffset staleCutoff, int batchSize);

    /// <summary>
    /// Returns a parameterized SQL statement that atomically claims up to
    /// <paramref name="batchSize"/> outbox rows, optionally applying a per-key
    /// head-of-line ordering predicate when <paramref name="orderingMode"/> is
    /// <see cref="OrderingMode.PerKey"/>.
    /// </summary>
    /// <param name="instanceId">
    /// The unique identifier of the calling dispatcher instance.
    /// Used as the <c>LockedBy</c> value on claimed rows.
    /// </param>
    /// <param name="now">The current UTC time used as the <c>LockedAt</c> value.</param>
    /// <param name="staleCutoff">
    /// Rows with <c>LockedAt &lt; staleCutoff</c> are treated as stale and eligible for re-claim.
    /// </param>
    /// <param name="batchSize">Maximum number of rows to claim in a single call.</param>
    /// <param name="orderingMode">
    /// The active ordering mode. When <see cref="OrderingMode.PerKey"/>, the dialect should
    /// apply a per-key head-of-line predicate so that only the oldest undelivered row of each
    /// key group is eligible. When <see cref="OrderingMode.None"/>, behavior must be
    /// bit-identical to <see cref="GetClaimSql(string, DateTimeOffset, DateTimeOffset, int)"/>.
    /// </param>
    /// <remarks>
    /// Custom dialects <b>must override</b> this 5-arg overload with a real head-of-line predicate to
    /// benefit from per-key ordering <b>and</b> declare the capability by returning
    /// <see langword="true"/> from <see cref="SupportsPerKeyHeadOfLineOrdering"/>; otherwise
    /// <see cref="OrderingMode.PerKey"/> is rejected at startup (the per-key ordering guard reads the
    /// capability flag — it does not, and cannot, infer ordering support from the shape of the SQL).
    /// The default implementation always delegates to
    /// <see cref="GetClaimSql(string, DateTimeOffset, DateTimeOffset, int)"/>, preserving
    /// backward compatibility for dialects that do not yet support ordering.
    /// </remarks>
    /// <returns>
    /// A <see cref="FormattableString"/> suitable for use with
    /// <see cref="Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlAsync"/>.
    /// All user-supplied values are passed as parameters — never interpolated directly.
    /// </returns>
    FormattableString GetClaimSql(
        string instanceId,
        DateTimeOffset now,
        DateTimeOffset staleCutoff,
        int batchSize,
        OrderingMode orderingMode)
        => GetClaimSql(instanceId, now, staleCutoff, batchSize);
}
