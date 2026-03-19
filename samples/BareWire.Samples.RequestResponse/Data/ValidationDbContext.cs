using Microsoft.EntityFrameworkCore;

namespace BareWire.Samples.RequestResponse.Data;

public sealed class ValidationDbContext(DbContextOptions<ValidationDbContext> options) : DbContext(options)
{
    public DbSet<ValidationRecord> ValidationRecords => Set<ValidationRecord>();
}
