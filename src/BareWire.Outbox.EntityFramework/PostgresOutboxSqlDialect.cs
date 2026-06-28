using BareWire.Abstractions.Outbox;

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
    public bool SupportsPerKeyHeadOfLineOrdering => true;

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

    /// <inheritdoc />
    public FormattableString GetClaimSql(
        string instanceId,
        DateTimeOffset now,
        DateTimeOffset staleCutoff,
        int batchSize,
        OrderingMode orderingMode)
    {
        // None: delegate to the 4-arg overload — claim SQL is bit-identical to pre-R7.7 (§2.1).
        if (orderingMode != OrderingMode.PerKey)
        {
            return GetClaimSql(instanceId, now, staleCutoff, batchSize);
        }

        // PerKey: correlated NOT EXISTS subquery that enforces head-of-line per key (ADR-025 §4).
        // - o."OrderingKey" IS NULL: keyless rows always pass through (no ordering constraint).
        // - NOT EXISTS (...): blocks a row when a strictly older undelivered row with the same key
        //   exists — that older row is the head and must be delivered first.
        // All user-supplied values are passed as FormattableString parameters (no interpolation
        // of values into SQL). The NOT EXISTS predicate is column-to-column (e."OrderingKey" =
        // o."OrderingKey") — no user value enters the predicate.
        return $"""
            UPDATE "OutboxMessages"
            SET "LockedAt" = {now}, "LockedBy" = {instanceId}
            WHERE "Id" IN (
              SELECT o."Id" FROM "OutboxMessages" o
              WHERE o."DeliveredAt" IS NULL
                AND (o."LockedAt" IS NULL OR o."LockedAt" < {staleCutoff})
                AND (
                  o."OrderingKey" IS NULL
                  OR NOT EXISTS (
                    SELECT 1 FROM "OutboxMessages" e
                    WHERE e."OrderingKey" = o."OrderingKey"
                      AND e."DeliveredAt" IS NULL
                      AND e."Id" < o."Id"
                  )
                )
              ORDER BY o."Id"
              LIMIT {batchSize}
              FOR UPDATE SKIP LOCKED
            )
            """;
    }
}
