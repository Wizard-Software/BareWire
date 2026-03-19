using Microsoft.EntityFrameworkCore;

namespace BareWire.Samples.BasicPublishConsume.Data;

public sealed class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    public DbSet<ReceivedMessage> ReceivedMessages => Set<ReceivedMessage>();
}
