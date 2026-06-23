namespace BareWire.Outbox.EntityFramework;

/// <summary>
/// Provider-specific SQL dialect for outbox row claim operations.
/// </summary>
/// <remarks>
/// Implement this interface to provide an atomic claim SQL statement for a database provider
/// other than PostgreSQL. The outbox store invokes <see cref="GetClaimSql"/> only when the active
/// context's provider matches <see cref="ProviderName"/>; for any other provider it falls back to a
/// non-atomic client-side claim (safe for a single dispatcher instance / testing, not for
/// multi-instance production). The default registration is <see cref="PostgresOutboxSqlDialect"/>
/// (PostgreSQL with <c>FOR UPDATE SKIP LOCKED</c>). Register a custom implementation via
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
    /// <see cref="GetClaimSql"/> only when this value equals the active provider's name; otherwise
    /// it uses the client-side fallback claim.
    /// </summary>
    string ProviderName { get; }

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
}
