using Microsoft.EntityFrameworkCore;

namespace BareWire.Samples.MultiConsumerPartitioning.Data;

/// <summary>
/// EF Core DbContext for persisting <see cref="ProcessingLogEntry"/> records.
/// </summary>
public sealed class PartitionDbContext(DbContextOptions<PartitionDbContext> options) : DbContext(options)
{
    /// <summary>All processing log entries persisted by the three event consumers.</summary>
    public DbSet<ProcessingLogEntry> ProcessingLog => Set<ProcessingLogEntry>();
}
