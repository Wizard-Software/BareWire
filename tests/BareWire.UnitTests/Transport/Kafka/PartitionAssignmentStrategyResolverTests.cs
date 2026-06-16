using AwesomeAssertions;
using BareWire.Transport.Kafka;
using BareWire.Transport.Kafka.Internal;
using Confluent.Kafka;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class PartitionAssignmentStrategyResolverTests
{
    // ── Happy-path mappings ───────────────────────────────────────────────────

    [Fact]
    public void Resolve_Range_MapsToConfluentRange()
    {
        // Arrange & Act
        PartitionAssignmentStrategy result =
            PartitionAssignmentStrategyResolver.Resolve(KafkaPartitionAssignmentStrategy.Range);

        // Assert
        result.Should().Be(PartitionAssignmentStrategy.Range);
    }

    [Fact]
    public void Resolve_RoundRobin_MapsToConfluentRoundRobin()
    {
        // Arrange & Act
        PartitionAssignmentStrategy result =
            PartitionAssignmentStrategyResolver.Resolve(KafkaPartitionAssignmentStrategy.RoundRobin);

        // Assert
        result.Should().Be(PartitionAssignmentStrategy.RoundRobin);
    }

    [Fact]
    public void Resolve_CooperativeSticky_MapsToConfluentCooperativeSticky()
    {
        // Arrange & Act
        PartitionAssignmentStrategy result =
            PartitionAssignmentStrategyResolver.Resolve(KafkaPartitionAssignmentStrategy.CooperativeSticky);

        // Assert
        result.Should().Be(PartitionAssignmentStrategy.CooperativeSticky);
    }

    // ── Default (D9) is CooperativeSticky ────────────────────────────────────

    [Fact]
    public void Resolve_DefaultStrategyFromOptions_IsCooperativeSticky()
    {
        // Arrange — new options uses the D9 default
        var options = new KafkaTransportOptions();

        // Act
        PartitionAssignmentStrategy result =
            PartitionAssignmentStrategyResolver.Resolve(options.ConsumerPartitionAssignmentStrategy);

        // Assert
        result.Should().Be(PartitionAssignmentStrategy.CooperativeSticky);
    }

    // ── Unknown value throws ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_UnknownStrategy_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var unknown = (KafkaPartitionAssignmentStrategy)999;

        // Act
        Action act = () => PartitionAssignmentStrategyResolver.Resolve(unknown);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("strategy");
    }
}
