namespace BareWire.Outbox.EntityFramework;

/// <summary>
/// PostgreSQL implementation of <see cref="IInboxSqlDialect"/> using <c>ON CONFLICT DO NOTHING</c>.
/// </summary>
internal sealed class PostgresInboxSqlDialect : IInboxSqlDialect
{
    public string ProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";

    public FormattableString GetUpsertSql(
        Guid messageId,
        string consumerType,
        DateTimeOffset receivedAt,
        DateTimeOffset expiresAt)
        => $"""
            INSERT INTO "InboxMessages" ("MessageId", "ConsumerType", "ReceivedAt", "ExpiresAt")
            VALUES ({messageId}, {consumerType}, {receivedAt}, {expiresAt})
            ON CONFLICT ("MessageId", "ConsumerType") DO NOTHING
            """;

    public FormattableString GetReLockSql(
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
