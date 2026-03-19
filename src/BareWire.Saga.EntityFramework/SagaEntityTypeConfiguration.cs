using BareWire.Abstractions.Saga;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BareWire.Saga.EntityFramework;

internal sealed class SagaEntityTypeConfiguration<TSaga> : IEntityTypeConfiguration<TSaga>
    where TSaga : class, ISagaState
{
    public void Configure(EntityTypeBuilder<TSaga> builder)
    {
        builder.HasKey(s => s.CorrelationId);

        builder.Property(s => s.CurrentState)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(s => s.Version)
            .IsConcurrencyToken();
    }
}
