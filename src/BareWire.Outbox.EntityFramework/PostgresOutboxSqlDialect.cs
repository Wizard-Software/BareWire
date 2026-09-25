using BareWire.Abstractions.Outbox;
using BareWire.Outbox.EntityFramework.Internal;

namespace BareWire.Outbox.EntityFramework;

/// <summary>
/// PostgreSQL implementation of <see cref="IOutboxSqlDialect"/> using
/// <c>FOR UPDATE SKIP LOCKED</c> for atomic, deadlock-free row claims.
/// </summary>
internal sealed class PostgresOutboxSqlDialect : IOutboxSqlDialect, INewRowsClaimSql, IDueOrderedRetryClaimSql
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

    // Never-claimed rows in id order. Every ORDER BY key follows the key order of IX_OutboxMessages_Claim
    // (DeliveredAt, LockedAt, Id). Within the predicate DeliveredAt and LockedAt are constantly NULL, so
    // the order equals ORDER BY "Id", while the matching keys let the planner read the rows as an ordered
    // index range stopped by LIMIT, without a Sort node.
    public FormattableString GetNewRowsClaimSql(
        string instanceId,
        DateTimeOffset now,
        int batchSize,
        OrderingMode orderingMode)
    {
        if (orderingMode != OrderingMode.PerKey)
        {
            return $"""
                UPDATE "OutboxMessages"
                SET "LockedAt" = {now}, "LockedBy" = {instanceId}
                WHERE "Id" IN (
                  SELECT "Id" FROM "OutboxMessages"
                  WHERE "DeliveredAt" IS NULL
                    AND "LockedAt" IS NULL
                  ORDER BY "DeliveredAt", "LockedAt", "Id"
                  LIMIT {batchSize}
                  FOR UPDATE SKIP LOCKED
                )
                """;
        }

        // The head-of-line predicate is identical to the public PerKey claim: a keyed row is claimable
        // only while no strictly older undelivered row with the same key exists.
        return $"""
            UPDATE "OutboxMessages"
            SET "LockedAt" = {now}, "LockedBy" = {instanceId}
            WHERE "Id" IN (
              SELECT o."Id" FROM "OutboxMessages" o
              WHERE o."DeliveredAt" IS NULL
                AND o."LockedAt" IS NULL
                AND (
                  o."OrderingKey" IS NULL
                  OR NOT EXISTS (
                    SELECT 1 FROM "OutboxMessages" e
                    WHERE e."OrderingKey" = o."OrderingKey"
                      AND e."DeliveredAt" IS NULL
                      AND e."Id" < o."Id"
                  )
                )
              ORDER BY o."DeliveredAt", o."LockedAt", o."Id"
              LIMIT {batchSize}
              FOR UPDATE SKIP LOCKED
            )
            """;
    }

    // Due retries in due-time order: a deferred nack and an abandoned claim both become claimable once
    // LockedAt < staleCutoff, so the earliest LockedAt is the longest-due row. A permanently rejected row
    // gets a later LockedAt on every attempt and moves to the back of this queue. The leading
    // "DeliveredAt" key is constant within the predicate; it only aligns ORDER BY with the key order of
    // IX_OutboxMessages_Claim so the range LockedAt < staleCutoff is read in order without a Sort node.
    public FormattableString GetDueOrderedRetryClaimSql(
        string instanceId,
        DateTimeOffset now,
        DateTimeOffset staleCutoff,
        int batchSize,
        OrderingMode orderingMode)
    {
        if (orderingMode != OrderingMode.PerKey)
        {
            return $"""
                UPDATE "OutboxMessages"
                SET "LockedAt" = {now}, "LockedBy" = {instanceId}
                WHERE "Id" IN (
                  SELECT "Id" FROM "OutboxMessages"
                  WHERE "DeliveredAt" IS NULL
                    AND "LockedAt" IS NOT NULL
                    AND "LockedAt" < {staleCutoff}
                  ORDER BY "DeliveredAt", "LockedAt", "Id"
                  LIMIT {batchSize}
                  FOR UPDATE SKIP LOCKED
                )
                """;
        }

        return $"""
            UPDATE "OutboxMessages"
            SET "LockedAt" = {now}, "LockedBy" = {instanceId}
            WHERE "Id" IN (
              SELECT o."Id" FROM "OutboxMessages" o
              WHERE o."DeliveredAt" IS NULL
                AND o."LockedAt" IS NOT NULL
                AND o."LockedAt" < {staleCutoff}
                AND (
                  o."OrderingKey" IS NULL
                  OR NOT EXISTS (
                    SELECT 1 FROM "OutboxMessages" e
                    WHERE e."OrderingKey" = o."OrderingKey"
                      AND e."DeliveredAt" IS NULL
                      AND e."Id" < o."Id"
                  )
                )
              ORDER BY o."DeliveredAt", o."LockedAt", o."Id"
              LIMIT {batchSize}
              FOR UPDATE SKIP LOCKED
            )
            """;
    }
}
