using System.Globalization;
using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Outbox.EntityFramework;
using Xunit;

namespace BareWire.UnitTests.Outbox;

/// <summary>
/// Tests for <see cref="IOutboxSqlDialect"/> and <see cref="PostgresOutboxSqlDialect"/>
/// covering the 5-arg ordering overload (R7.7.4), the default-OFF invariant (§2.1), and the
/// Default Interface Method passthrough guarantee (§2.3).
/// </summary>
public sealed class OutboxSqlDialectTests
{
    // Synchronized arguments — the same values are used across all assertions so that SQL
    // differences are purely structural (not a result of differing parameter values).
    private static readonly string InstanceId = "test-instance";
    private static readonly DateTimeOffset Now = new(2025, 1, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StaleCutoff = new(2025, 1, 15, 9, 55, 0, TimeSpan.Zero);
    private const int BatchSize = 50;

    private readonly PostgresOutboxSqlDialect _sut = new();

    // -------------------------------------------------------------------------
    // U4 — default-OFF snapshot: 4-arg and 5-arg(None) produce identical SQL
    // -------------------------------------------------------------------------

    [Fact]
    public void GetClaimSql_4Arg_MatchesPreR77Snapshot()
    {
        // Arrange — snapshot of the 4-arg (pre-R7.7) SQL. This test will fail if the base claim
        // SQL is accidentally modified, confirming the default-OFF invariant (§2.1).
        // The date format used by FormattableString.ToString(InvariantCulture) on DateTimeOffset
        // is "MM/dd/yyyy HH:mm:ss zzz" (e.g. "01/15/2025 10:00:00 +00:00").
        const string expectedSnapshot =
            """
            UPDATE "OutboxMessages"
            SET "LockedAt" = 01/15/2025 10:00:00 +00:00, "LockedBy" = test-instance
            WHERE "Id" IN (
              SELECT "Id" FROM "OutboxMessages"
              WHERE "DeliveredAt" IS NULL
                AND ("LockedAt" IS NULL OR "LockedAt" < 01/15/2025 09:55:00 +00:00)
              ORDER BY "Id"
              LIMIT 50
              FOR UPDATE SKIP LOCKED
            )
            """;

        // Act
        FormattableString sql = _sut.GetClaimSql(InstanceId, Now, StaleCutoff, BatchSize);
        string rendered = sql.ToString(CultureInfo.InvariantCulture);

        // Assert — must match the pre-R7.7 snapshot exactly (no OrderingKey in predicate).
        rendered.Should().Be(expectedSnapshot,
            "the 4-arg GetClaimSql must be bit-identical to the pre-R7.7 claim SQL");
    }

    [Fact]
    public void GetClaimSql_5ArgNone_IsIdenticalTo4Arg()
    {
        // Arrange
        FormattableString fourArg = _sut.GetClaimSql(InstanceId, Now, StaleCutoff, BatchSize);
        string fourArgSql = fourArg.ToString(CultureInfo.InvariantCulture);

        // Act
        FormattableString fiveArgNone = _sut.GetClaimSql(
            InstanceId, Now, StaleCutoff, BatchSize, OrderingMode.None);
        string fiveArgNoneSql = fiveArgNone.ToString(CultureInfo.InvariantCulture);

        // Assert — None must be bit-identical to the 4-arg (default-OFF invariant §2.1).
        fiveArgNoneSql.Should().Be(fourArgSql,
            "5-arg GetClaimSql with OrderingMode.None must produce SQL identical to the 4-arg overload");

        // Double-check: neither result contains OrderingKey references.
        fiveArgNoneSql.Should().NotContain("OrderingKey",
            "None mode must not reference the OrderingKey column");
    }

    // -------------------------------------------------------------------------
    // U5 — 5-arg(PerKey) PostgreSQL contains NOT EXISTS and OrderingKey IS NULL
    // -------------------------------------------------------------------------

    [Fact]
    public void GetClaimSql_5ArgPerKey_ContainsNotExistsPredicate()
    {
        // Act
        FormattableString sql = _sut.GetClaimSql(
            InstanceId, Now, StaleCutoff, BatchSize, OrderingMode.PerKey);
        string rendered = sql.ToString(CultureInfo.InvariantCulture);

        // Assert — structural presence of the NOT EXISTS head-of-line predicate (ADR-025 §4).
        rendered.Should().Contain("NOT EXISTS",
            "PerKey SQL must contain the NOT EXISTS correlated subquery predicate");

        rendered.Should().Contain("\"OrderingKey\" IS NULL",
            "PerKey SQL must allow keyless rows to pass through via OR o.\"OrderingKey\" IS NULL");

        rendered.Should().Contain("e.\"OrderingKey\" = o.\"OrderingKey\"",
            "PerKey NOT EXISTS predicate must compare columns (not user values) for the key join");

        rendered.Should().Contain("e.\"DeliveredAt\" IS NULL",
            "NOT EXISTS subquery must filter for undelivered rows only");

        rendered.Should().Contain("e.\"Id\" < o.\"Id\"",
            "NOT EXISTS predicate must block rows with older siblings (e.Id < o.Id)");
    }

    [Fact]
    public void GetClaimSql_5ArgPerKey_DoesNotContainUserValueInPredicate()
    {
        // SQL injection guard: verify that no user-supplied value (instanceId, numeric batchSize
        // literal, etc.) appears inside the NOT EXISTS correlated subquery.
        FormattableString sql = _sut.GetClaimSql(
            InstanceId, Now, StaleCutoff, BatchSize, OrderingMode.PerKey);
        string rendered = sql.ToString(CultureInfo.InvariantCulture);

        // The predicate is column-to-column — none of the user-controlled strings enter it.
        // This is a structural safety check: ensure the instance id does not appear in
        // the correlated subquery (it appears only in the outer SET clause).
        int notExistsPos = rendered.IndexOf("NOT EXISTS", StringComparison.Ordinal);
        notExistsPos.Should().BeGreaterThan(0, "NOT EXISTS must be present");

        string notExistsBlock = rendered[notExistsPos..];
        notExistsBlock.Should().NotContain(InstanceId,
            "the NOT EXISTS predicate must not contain the instanceId user value");
    }

    // -------------------------------------------------------------------------
    // U6 — DIM: a custom dialect that only implements the 4-arg overload
    //       compiles and its 5-arg(PerKey) degrades to passthrough (non-breaking)
    // -------------------------------------------------------------------------

    [Fact]
    public void CustomDialect_4ArgOnly_PerKeyDegradesToPassthrough()
    {
        // Arrange — a custom dialect that overrides only the 4-arg overload (simulates an
        // existing third-party SqlServer dialect that predates R7.7).
        IOutboxSqlDialect dialect = new FourArgOnlyDialect();

        // Act — invoke via the 4-arg and 5-arg(PerKey) overloads using the same arguments.
        FormattableString fourArgSql = dialect.GetClaimSql(InstanceId, Now, StaleCutoff, BatchSize);
        FormattableString fiveArgPerKeySql = dialect.GetClaimSql(
            InstanceId, Now, StaleCutoff, BatchSize, OrderingMode.PerKey);

        // Assert — the 5-arg(PerKey) DIM passthrough must delegate to the 4-arg.
        // The SQL contains no key-affinity predicate — graceful degrade, not a hard failure.
        string fourArgRendered = fourArgSql.ToString(CultureInfo.InvariantCulture);
        string fiveArgRendered = fiveArgPerKeySql.ToString(CultureInfo.InvariantCulture);

        fiveArgRendered.Should().Be(fourArgRendered,
            "a custom dialect that only overrides the 4-arg overload must degrade transparently " +
            "to passthrough for PerKey via the DIM");

        fiveArgRendered.Should().NotContain("NOT EXISTS",
            "passthrough must not produce any per-key ordering predicate");
    }

    /// <summary>
    /// Minimal custom dialect that implements only the 4-arg overload (pre-R7.7 contract).
    /// Proves DIM backward-compatibility: it compiles without implementing the 5-arg method.
    /// </summary>
    private sealed class FourArgOnlyDialect : IOutboxSqlDialect
    {
        public string ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";

        public FormattableString GetClaimSql(
            string instanceId,
            DateTimeOffset now,
            DateTimeOffset staleCutoff,
            int batchSize)
            => $"""
                UPDATE [OutboxMessages]
                SET [LockedAt] = {now}, [LockedBy] = {instanceId}
                WHERE [Id] IN (
                  SELECT TOP ({batchSize}) [Id] FROM [OutboxMessages]
                  WITH (UPDLOCK, READPAST)
                  WHERE [DeliveredAt] IS NULL
                    AND ([LockedAt] IS NULL OR [LockedAt] < {staleCutoff})
                  ORDER BY [Id]
                )
                """;
        // NOTE: 5-arg GetClaimSql is intentionally not overridden — the DIM provides passthrough.
    }
}
