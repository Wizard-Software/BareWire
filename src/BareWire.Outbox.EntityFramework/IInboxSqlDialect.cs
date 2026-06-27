namespace BareWire.Outbox.EntityFramework;

/// <summary>
/// Provider-specific SQL dialect for inbox upsert and atomic re-lock operations.
/// </summary>
/// <remarks>
/// <see cref="EfCoreInboxStore"/> uses the dialect's atomic <see cref="GetReLockSql"/> only when
/// <see cref="ProviderName"/> matches the active EF Core provider (compared via the base
/// <c>DatabaseFacade.ProviderName</c> API). For any other provider it falls back to a non-atomic
/// client-side read-then-update re-lock — safe for a single dispatcher instance / testing, not for
/// multi-instance production. To get the atomic re-lock on a provider other than PostgreSQL (e.g.
/// SQL Server), register a custom <see cref="IInboxSqlDialect"/> whose <see cref="ProviderName"/>
/// matches that provider. This mirrors <c>IOutboxSqlDialect</c> exactly.
/// </remarks>
public interface IInboxSqlDialect
{
    /// <summary>
    /// The EF Core provider name this dialect targets — compared against
    /// <c>DbContext.Database.ProviderName</c> (e.g. <c>"Npgsql.EntityFrameworkCore.PostgreSQL"</c>,
    /// <c>"Microsoft.EntityFrameworkCore.SqlServer"</c>). The inbox store calls
    /// <see cref="GetReLockSql"/> only when this value equals the active provider's name; otherwise
    /// it uses the non-atomic client-side re-lock fallback.
    /// </summary>
    /// <remarks>
    /// The default targets PostgreSQL (the shipped dialect). Custom dialects targeting another
    /// provider <b>must</b> override this so the store actually invokes their atomic re-lock SQL.
    /// </remarks>
    string ProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// Returns a parameterized upsert SQL that inserts a new inbox message
    /// or does nothing if the composite key already exists.
    /// The caller uses the rows-affected count to determine whether the insert succeeded (1) or was a duplicate (0).
    /// </summary>
    FormattableString GetUpsertSql(
        Guid messageId,
        string consumerType,
        DateTimeOffset receivedAt,
        DateTimeOffset expiresAt);

    /// <summary>
    /// Returns a parameterized UPDATE that atomically re-acquires an EXISTING inbox lock only when
    /// it is expired (<c>ExpiresAt &lt; now</c>) AND unprocessed (<c>ProcessedAt IS NULL</c>). The
    /// caller treats a rows-affected count of <c>1</c> as a successful re-lock and <c>0</c> as "lost
    /// the race / not eligible". Evaluating the predicate inside a single UPDATE statement makes the
    /// re-lock atomic: concurrent workers racing on the same expired row contend on the row lock, so
    /// exactly one UPDATE matches.
    /// </summary>
    /// <param name="messageId">The inbox message identifier (composite key part).</param>
    /// <param name="consumerType">The consumer type (composite key part).</param>
    /// <param name="now">The current UTC time used for the <c>ExpiresAt &lt; now</c> expiry check.</param>
    /// <param name="newExpiresAt">The new expiry to set on a successful re-lock.</param>
    /// <remarks>
    /// The default implementation targets ANSI SQL with double-quoted identifiers (PostgreSQL /
    /// SQLite). Providers requiring different quoting (e.g. SQL Server brackets) override it. All
    /// user-supplied values are passed as parameters — never interpolated directly.
    /// </remarks>
    FormattableString GetReLockSql(
        Guid messageId,
        string consumerType,
        DateTimeOffset now,
        DateTimeOffset newExpiresAt)
        => $"""
            UPDATE "InboxMessages"
            SET "ExpiresAt" = {newExpiresAt}
            WHERE "MessageId" = {messageId}
              AND "ConsumerType" = {consumerType}
              AND "ExpiresAt" < {now}
              AND "ProcessedAt" IS NULL
            """;
}
