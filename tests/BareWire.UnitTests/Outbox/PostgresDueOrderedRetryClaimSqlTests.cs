using System.Text.RegularExpressions;
using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using Xunit;

namespace BareWire.UnitTests.Outbox;

/// <summary>
/// Shape tests for the single-class claim statements <see cref="PostgresOutboxSqlDialect.GetNewRowsClaimSql"/>
/// and <see cref="PostgresOutboxSqlDialect.GetDueOrderedRetryClaimSql"/> — the index-ordered
/// statements a fair claim cycle uses instead of the combined public claim statement.
/// </summary>
public sealed class PostgresDueOrderedRetryClaimSqlTests
{
    private static readonly string InstanceId = "test-instance";
    private static readonly DateTimeOffset Now = new(2025, 1, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StaleCutoff = new(2025, 1, 15, 9, 55, 0, TimeSpan.Zero);
    private const int BatchSize = 50;

    private readonly PostgresOutboxSqlDialect _sut = new();

    // ── GetDueOrderedRetryClaimSql — None ────────────────────────────────────

    [Fact]
    public void GetDueOrderedRetryClaimSql_None_ContainsDueRetryPredicateOrderAndLocking()
    {
        FormattableString sql = _sut.GetDueOrderedRetryClaimSql(
            InstanceId, Now, StaleCutoff, BatchSize, OrderingMode.None);
        string normalized = NormalizeOrderBy(sql.Format);

        normalized.Should().Contain("\"LockedAt\" IS NOT NULL");
        normalized.Should().Contain("\"LockedAt\" < {");
        normalized.Should().Contain("ORDER BY \"DeliveredAt\", \"LockedAt\", \"Id\"");
        normalized.Should().Contain("FOR UPDATE SKIP LOCKED");
        normalized.Should().NotContain("\"LockedAt\" IS NULL OR");
    }

    [Fact]
    public void GetDueOrderedRetryClaimSql_None_ArgumentsAreNowInstanceIdStaleCutoffBatchSize()
    {
        FormattableString sql = _sut.GetDueOrderedRetryClaimSql(
            InstanceId, Now, StaleCutoff, BatchSize, OrderingMode.None);

        sql.GetArguments().Should().Equal(Now, InstanceId, StaleCutoff, BatchSize);
        sql.Format.Should().NotContain(InstanceId, "user-supplied values must be arguments, not literals in Format");
    }

    [Fact]
    public void GetDueOrderedRetryClaimSql_None_DoesNotContainNotExists()
    {
        FormattableString sql = _sut.GetDueOrderedRetryClaimSql(
            InstanceId, Now, StaleCutoff, BatchSize, OrderingMode.None);

        sql.Format.Should().NotContain("NOT EXISTS");
    }

    // ── GetDueOrderedRetryClaimSql — PerKey ──────────────────────────────────

    [Fact]
    public void GetDueOrderedRetryClaimSql_PerKey_ContainsDueRetryPredicateOrderAndLocking()
    {
        FormattableString sql = _sut.GetDueOrderedRetryClaimSql(
            InstanceId, Now, StaleCutoff, BatchSize, OrderingMode.PerKey);
        string normalized = NormalizeOrderBy(sql.Format);

        normalized.Should().Contain("\"LockedAt\" IS NOT NULL");
        normalized.Should().Contain("\"LockedAt\" < {");
        normalized.Should().Contain("ORDER BY \"DeliveredAt\", \"LockedAt\", \"Id\"");
        normalized.Should().Contain("FOR UPDATE SKIP LOCKED");
        normalized.Should().NotContain("\"LockedAt\" IS NULL OR");
    }

    [Fact]
    public void GetDueOrderedRetryClaimSql_PerKey_ArgumentsAreNowInstanceIdStaleCutoffBatchSize()
    {
        FormattableString sql = _sut.GetDueOrderedRetryClaimSql(
            InstanceId, Now, StaleCutoff, BatchSize, OrderingMode.PerKey);

        sql.GetArguments().Should().Equal(Now, InstanceId, StaleCutoff, BatchSize);
    }

    [Fact]
    public void GetDueOrderedRetryClaimSql_PerKey_ContainsHeadOfLinePredicate()
    {
        FormattableString sql = _sut.GetDueOrderedRetryClaimSql(
            InstanceId, Now, StaleCutoff, BatchSize, OrderingMode.PerKey);
        string rendered = sql.Format;

        rendered.Should().Contain("o.\"OrderingKey\" IS NULL");
        rendered.Should().Contain("NOT EXISTS");
        rendered.Should().Contain("e.\"OrderingKey\" = o.\"OrderingKey\"");
        rendered.Should().Contain("e.\"Id\" < o.\"Id\"");
        rendered.Should().Contain("ORDER BY o.\"DeliveredAt\", o.\"LockedAt\", o.\"Id\"");
    }

    // ── GetNewRowsClaimSql — None ─────────────────────────────────────────────

    [Fact]
    public void GetNewRowsClaimSql_None_ContainsNewRowsPredicateOrderAndLocking()
    {
        FormattableString sql = _sut.GetNewRowsClaimSql(InstanceId, Now, BatchSize, OrderingMode.None);
        string normalized = NormalizeOrderBy(sql.Format);

        normalized.Should().Contain("\"LockedAt\" IS NULL");
        normalized.Should().NotContain(" OR \"LockedAt\"");
        normalized.Should().Contain("ORDER BY \"DeliveredAt\", \"LockedAt\", \"Id\"");
        normalized.Should().Contain("FOR UPDATE SKIP LOCKED");
    }

    [Fact]
    public void GetNewRowsClaimSql_None_ArgumentsAreNowInstanceIdBatchSize()
    {
        FormattableString sql = _sut.GetNewRowsClaimSql(InstanceId, Now, BatchSize, OrderingMode.None);

        sql.GetArguments().Should().Equal(Now, InstanceId, BatchSize);
        sql.Format.Should().NotContain(InstanceId, "user-supplied values must be arguments, not literals in Format");
    }

    [Fact]
    public void GetNewRowsClaimSql_None_DoesNotContainNotExists()
    {
        FormattableString sql = _sut.GetNewRowsClaimSql(InstanceId, Now, BatchSize, OrderingMode.None);

        sql.Format.Should().NotContain("NOT EXISTS");
    }

    // ── GetNewRowsClaimSql — PerKey ───────────────────────────────────────────

    [Fact]
    public void GetNewRowsClaimSql_PerKey_ContainsNewRowsPredicateOrderAndLocking()
    {
        FormattableString sql = _sut.GetNewRowsClaimSql(InstanceId, Now, BatchSize, OrderingMode.PerKey);
        string normalized = NormalizeOrderBy(sql.Format);

        normalized.Should().Contain("\"LockedAt\" IS NULL");
        normalized.Should().NotContain(" OR \"LockedAt\"");
        normalized.Should().Contain("ORDER BY \"DeliveredAt\", \"LockedAt\", \"Id\"");
        normalized.Should().Contain("FOR UPDATE SKIP LOCKED");
    }

    [Fact]
    public void GetNewRowsClaimSql_PerKey_ArgumentsAreNowInstanceIdBatchSize()
    {
        FormattableString sql = _sut.GetNewRowsClaimSql(InstanceId, Now, BatchSize, OrderingMode.PerKey);

        sql.GetArguments().Should().Equal(Now, InstanceId, BatchSize);
    }

    [Fact]
    public void GetNewRowsClaimSql_PerKey_ContainsHeadOfLinePredicate()
    {
        FormattableString sql = _sut.GetNewRowsClaimSql(InstanceId, Now, BatchSize, OrderingMode.PerKey);
        string rendered = sql.Format;

        rendered.Should().Contain("o.\"OrderingKey\" IS NULL");
        rendered.Should().Contain("NOT EXISTS");
        rendered.Should().Contain("e.\"OrderingKey\" = o.\"OrderingKey\"");
        rendered.Should().Contain("e.\"Id\" < o.\"Id\"");
        rendered.Should().Contain("ORDER BY o.\"DeliveredAt\", o.\"LockedAt\", o.\"Id\"");
    }

    // ── Capability declarations ───────────────────────────────────────────────

    [Fact]
    public void GetDueOrderedRetryClaimSql_BuiltInPostgresDialect_IsImplemented()
    {
        IOutboxSqlDialect dialect = new PostgresOutboxSqlDialect();

        dialect.Should().BeAssignableTo<IDueOrderedRetryClaimSql>();
    }

    [Fact]
    public void GetNewRowsClaimSql_BuiltInPostgresDialect_IsImplemented()
    {
        IOutboxSqlDialect dialect = new PostgresOutboxSqlDialect();

        dialect.Should().BeAssignableTo<INewRowsClaimSql>();
    }

    // Removes the "o." correlation alias used by the PerKey overloads and collapses whitespace, so
    // the same ORDER BY assertion applies to both the None and PerKey renderings of a statement.
    private static string NormalizeOrderBy(string format)
        => Regex.Replace(format.Replace("o.\"", "\"", StringComparison.Ordinal), @"\s+", " ");
}
