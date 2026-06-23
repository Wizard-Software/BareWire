namespace BareWire.Outbox.EntityFramework;

/// <summary>
/// PostgreSQL implementation of <see cref="IOutboxSqlDialect"/> using
/// <c>FOR UPDATE SKIP LOCKED</c> for atomic, deadlock-free row claims.
/// </summary>
internal sealed class PostgresOutboxSqlDialect : IOutboxSqlDialect
{
    /// <inheritdoc />
    public string ProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <inheritdoc />
    public FormattableString GetClaimSql(
        string instanceId,
        DateTimeOffset now,
        DateTimeOffset staleCutoff,
        int batchSize)
        => $"""
            UPDATE "OutboxMessages"
            SET "LockedAt" = {now}, "LockedBy" = {instanceId}
            WHERE "Id" IN (
              SELECT "Id" FROM "OutboxMessages"
              WHERE "DeliveredAt" IS NULL
                AND ("LockedAt" IS NULL OR "LockedAt" < {staleCutoff})
              ORDER BY "Id"
              LIMIT {batchSize}
              FOR UPDATE SKIP LOCKED
            )
            """;
}
