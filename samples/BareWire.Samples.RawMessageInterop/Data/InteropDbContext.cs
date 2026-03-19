using Microsoft.EntityFrameworkCore;

namespace BareWire.Samples.RawMessageInterop.Data;

/// <summary>
/// EF Core database context for the RawMessageInterop sample.
/// Persists messages processed by both the raw and typed interop consumers.
/// </summary>
public sealed class InteropDbContext(DbContextOptions<InteropDbContext> options) : DbContext(options)
{
    /// <summary>Gets the set of processed messages.</summary>
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
}
