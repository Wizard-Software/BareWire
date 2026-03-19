using Microsoft.EntityFrameworkCore;

namespace BareWire.Samples.RetryAndDlq.Data;

/// <summary>
/// EF Core database context for the RetryAndDlq sample.
/// Stores <see cref="FailedPayment"/> records written by <c>DlqConsumer</c>.
/// </summary>
public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    /// <summary>Gets the set of failed payments recorded from the dead-letter queue.</summary>
    public DbSet<FailedPayment> FailedPayments => Set<FailedPayment>();
}
