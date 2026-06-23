using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BareWire.Outbox.EntityFramework;

internal sealed class OutboxMessageEntityTypeConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id)
            .ValueGeneratedOnAdd();

        builder.Property(m => m.MessageId)
            .IsRequired();

        builder.Property(m => m.DestinationAddress)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(m => m.ContentType)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(m => m.Payload)
            .IsRequired();

        builder.Property(m => m.CreatedAt)
            .IsRequired();

        builder.Property(m => m.LockedBy)
            .HasMaxLength(256);

        // Nullable — NULL means keyless/passthrough. The conditional ordering index
        // (IX_OutboxMessages_Ordering) is applied separately by OutboxModelCustomizer
        // so it is only created when OrderingMode == PerKey.
        builder.Property(m => m.OrderingKey)
            .HasMaxLength(256);

        builder.HasIndex(m => m.MessageId);

        builder.HasIndex(m => m.CreatedAt);

        builder.HasIndex(m => m.DeliveredAt);

        // Composite index used by the claim query: (DeliveredAt, LockedAt, Id).
        // Filtered variant for PostgreSQL is configured in OutboxDbContext.OnModelCreating
        // based on the active provider, since HasFilter uses PG-specific quoting that
        // breaks the SQLite schema path.
        builder.HasIndex(m => new { m.DeliveredAt, m.LockedAt, m.Id })
            .HasDatabaseName("IX_OutboxMessages_Claim");

        // Supports the post-claim SELECT in GetPendingAsync
        // (WHERE LockedBy = {instanceId} AND DeliveredAt IS NULL ORDER BY Id).
        // LockedBy is the lead column so each instance reads only its own claimed-undelivered
        // rows without a heap post-filter on LockedBy. Unfiltered so it is valid on every
        // provider (SQLite + PostgreSQL).
        builder.HasIndex(m => new { m.LockedBy, m.DeliveredAt, m.Id })
            .HasDatabaseName("IX_OutboxMessages_LockedBy");
    }
}
