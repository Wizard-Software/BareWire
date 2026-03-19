using BareWire.Samples.TransactionalOutbox.Models;
using Microsoft.EntityFrameworkCore;

namespace BareWire.Samples.TransactionalOutbox.Data;

/// <summary>
/// EF Core DbContext for the TransactionalOutbox sample.
/// Shares the same PostgreSQL database as <see cref="BareWire.Outbox.EntityFramework.OutboxDbContext"/>
/// so that business writes and outbox messages are committed in the same transaction.
/// </summary>
public sealed class TransferDbContext(DbContextOptions<TransferDbContext> options) : DbContext(options)
{
    public DbSet<Transfer> Transfers => Set<Transfer>();
}
