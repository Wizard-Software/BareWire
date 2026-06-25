using Microsoft.EntityFrameworkCore;

namespace BareWire.Samples.OrderedConsumers.Data;

/// <summary>
/// EF Core DbContext for persisting <see cref="ProcessedRecord"/> rows produced by
/// the ordered consumer instances.
/// </summary>
public sealed class OrderedConsumersDbContext(DbContextOptions<OrderedConsumersDbContext> options)
    : DbContext(options)
{
    /// <summary>All processed message records written by the ordered consumers.</summary>
    public DbSet<ProcessedRecord> ProcessedRecords => Set<ProcessedRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedRecord>(entity =>
        {
            entity.ToTable("ordered_consumers_processed");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
        });
    }
}
