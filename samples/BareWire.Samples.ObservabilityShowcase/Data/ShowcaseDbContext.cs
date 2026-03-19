using Microsoft.EntityFrameworkCore;

namespace BareWire.Samples.ObservabilityShowcase.Data;

/// <summary>
/// Entity Framework Core DbContext shared by the observability showcase sample.
/// Used as the application DbContext for any sample-specific entities.
/// The saga tables are managed by <c>SagaDbContext</c> and the outbox tables
/// by <c>OutboxDbContext</c>, both registered separately via their respective
/// <c>AddBareWireSaga</c> / <c>AddBareWireOutbox</c> extension methods.
/// </summary>
public sealed class ShowcaseDbContext(DbContextOptions<ShowcaseDbContext> options) : DbContext(options)
{
    // No application-specific DbSets are required for this showcase sample.
    // The saga and outbox tables are managed by their own DbContext registrations.
}
