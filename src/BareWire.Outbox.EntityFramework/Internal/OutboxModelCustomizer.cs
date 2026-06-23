using BareWire.Abstractions.Outbox;
using BareWire.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BareWire.Outbox.EntityFramework.Internal;

/// <summary>
/// EF Core model customizer that conditionally adds the partial ordering index
/// <c>IX_OutboxMessages_Ordering</c> on <c>(OrderingKey, Id) WHERE DeliveredAt IS NULL</c>.
/// The index is only created when <see cref="OutboxOptions.OrderingMode"/> is
/// <see cref="OrderingMode.PerKey"/> and the active provider is PostgreSQL; it is absent
/// in the default <see cref="OrderingMode.None"/> mode (no write amplification).
/// </summary>
/// <remarks>
/// Implements variant B of OQ-1 from the R7.7 plan: the index is applied outside of
/// <see cref="OutboxDbContext.OnModelCreating"/> so the DbContext constructor remains
/// untouched (accepts only <see cref="Microsoft.EntityFrameworkCore.DbContextOptions{TContext}"/>).
/// <para>
/// Delegates to the base <see cref="RelationalModelCustomizer"/> first so that all
/// standard EF Core model configuration is applied before the ordering index is appended.
/// </para>
/// </remarks>
internal sealed class OutboxModelCustomizer : IModelCustomizer
{
    private const string PostgreSqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly IModelCustomizer _base;
    private readonly OutboxOptions _options;

    internal OutboxModelCustomizer(IModelCustomizer @base, OutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(@base);
        ArgumentNullException.ThrowIfNull(options);

        _base = @base;
        _options = options;
    }

    /// <inheritdoc />
    public void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        // Apply all standard EF Core model customizations first.
        _base.Customize(modelBuilder, context);

        // Only add the ordering index when PerKey mode is active on a PostgreSQL provider.
        // This preserves the default-OFF invariant: OrderingMode.None produces zero extra
        // indexes and zero write amplification — bit-identical to pre-R7.7 behavior.
        if (_options.OrderingMode != OrderingMode.PerKey)
        {
            return;
        }

        if (!string.Equals(context.Database.ProviderName, PostgreSqlProviderName, StringComparison.Ordinal))
        {
            return;
        }

        // Partial index on (OrderingKey, Id) for undelivered rows. Speeds up the
        // NOT EXISTS correlated subquery in PostgresOutboxSqlDialect.GetClaimSql (PerKey):
        //   e.OrderingKey = o.OrderingKey AND e.DeliveredAt IS NULL AND e.Id < o.Id
        // PostgreSQL double-quoting is required to preserve case in identifiers.
        modelBuilder.Entity<OutboxMessage>()
            .HasIndex(m => new { m.OrderingKey, m.Id })
            .HasDatabaseName("IX_OutboxMessages_Ordering")
            .HasFilter("\"DeliveredAt\" IS NULL");
    }
}
