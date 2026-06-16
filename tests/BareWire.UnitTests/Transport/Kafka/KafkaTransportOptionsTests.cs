using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.Kafka;
using Confluent.Kafka;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaTransportOptionsTests
{
    // ── Validate (producer) ───────────────────────────────────────────────────

    [Fact]
    public void Validate_NullBootstrapServers_ThrowsBareWireConfigurationException()
    {
        // Arrange
        var options = new KafkaTransportOptions { BootstrapServers = null! };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaTransportOptions.BootstrapServers));
    }

    [Fact]
    public void Validate_EmptyBootstrapServers_ThrowsBareWireConfigurationException()
    {
        // Arrange
        var options = new KafkaTransportOptions { BootstrapServers = string.Empty };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaTransportOptions.BootstrapServers));
    }

    [Fact]
    public void Validate_ValidBootstrapServers_DoesNotThrow()
    {
        // Arrange
        var options = new KafkaTransportOptions { BootstrapServers = "localhost:9092" };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().NotThrow();
    }

    // ── ValidateConsumer ──────────────────────────────────────────────────────

    [Fact]
    public void ValidateConsumer_WhenGroupIdEmpty_ThrowsBareWireConfigurationException()
    {
        // Arrange — BootstrapServers valid; GroupId empty (default)
        var options = new KafkaTransportOptions { BootstrapServers = "localhost:9092" };

        // Act
        Action act = () => options.ValidateConsumer();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaTransportOptions.GroupId));
    }

    [Fact]
    public void ValidateConsumer_NullGroupId_ThrowsBareWireConfigurationException()
    {
        // Arrange
        var options = new KafkaTransportOptions
        {
            BootstrapServers = "localhost:9092",
            GroupId = null!,
        };

        // Act
        Action act = () => options.ValidateConsumer();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaTransportOptions.GroupId));
    }

    [Fact]
    public void ValidateConsumer_NonEmptyGroupId_DoesNotThrow()
    {
        // Arrange
        var options = new KafkaTransportOptions
        {
            BootstrapServers = "localhost:9092",
            GroupId = "my-service-group",
        };

        // Act
        Action act = () => options.ValidateConsumer();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WithoutGroupId_DoesNotThrow()
    {
        // Arrange — producer-only path; GroupId is empty by default
        var options = new KafkaTransportOptions { BootstrapServers = "localhost:9092" };

        // Act — Validate() must NOT enforce GroupId (producer-only DI tests must still pass)
        Action act = () => options.Validate();

        // Assert
        act.Should().NotThrow();
    }

    // ── Default values (producer) ─────────────────────────────────────────────

    [Fact]
    public void DefaultValues_EnableIdempotence_IsTrue()
    {
        // Arrange & Act
        var options = new KafkaTransportOptions();

        // Assert
        options.EnableIdempotence.Should().BeTrue();
    }

    [Fact]
    public void DefaultValues_Acks_IsAll()
    {
        // Arrange & Act
        var options = new KafkaTransportOptions();

        // Assert
        options.Acks.Should().Be(Acks.All);
    }

    [Fact]
    public void DefaultValues_MaxInFlight_IsAtMostFive()
    {
        // Arrange & Act
        var options = new KafkaTransportOptions();

        // Assert — must be <= 5 for idempotent producer correctness
        options.MaxInFlight.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public void DefaultValues_OptionalProperties_AreNull()
    {
        // Arrange & Act
        var options = new KafkaTransportOptions();

        // Assert — null means "use librdkafka default", not coerced to 0
        options.MessageSendMaxRetries.Should().BeNull();
        options.LingerMs.Should().BeNull();
        options.BatchSize.Should().BeNull();
        options.QueueBufferingMaxMessages.Should().BeNull();
        options.QueueBufferingMaxKbytes.Should().BeNull();
    }

    [Fact]
    public void DefaultValues_FlushTimeout_IsTenSeconds()
    {
        // Arrange & Act
        var options = new KafkaTransportOptions();

        // Assert
        options.FlushTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    // ── Default consumer values (D6 Confluent recommendation) ────────────────

    [Fact]
    public void DefaultValues_EnableAutoOffsetStore_IsFalse()
    {
        // Arrange & Act
        var options = new KafkaTransportOptions();

        // Assert — D6: manual StoreOffset after settlement; librdkafka must NOT auto-store
        options.EnableAutoOffsetStore.Should().BeFalse();
    }

    [Fact]
    public void DefaultValues_EnableAutoCommit_IsTrue()
    {
        // Arrange & Act
        var options = new KafkaTransportOptions();

        // Assert — D6: background commit after manual StoreOffset
        options.EnableAutoCommit.Should().BeTrue();
    }

    [Fact]
    public void DefaultValues_AutoOffsetReset_IsEarliest()
    {
        // Arrange & Act
        var options = new KafkaTransportOptions();

        // Assert
        options.AutoOffsetReset.Should().Be(AutoOffsetReset.Earliest);
    }

    [Fact]
    public void DefaultValues_ConsumerPartitionAssignmentStrategy_IsCooperativeSticky()
    {
        // Arrange & Act
        var options = new KafkaTransportOptions();

        // Assert — D9
        options.ConsumerPartitionAssignmentStrategy.Should().Be(KafkaPartitionAssignmentStrategy.CooperativeSticky);
    }

    [Fact]
    public void DefaultValues_ConsumerOptionalTimeouts_AreNull()
    {
        // Arrange & Act
        var options = new KafkaTransportOptions();

        // Assert — null means "use librdkafka default"
        options.SessionTimeoutMs.Should().BeNull();
        options.MaxPollIntervalMs.Should().BeNull();
    }

    [Fact]
    public void ValidateConsumer_WhenGroupIdNull_ThrowsBareWireConfigurationException()
    {
        // Arrange
        var options = new KafkaTransportOptions
        {
            BootstrapServers = "localhost:9092",
            GroupId = null!,
        };

        // Act
        Action act = () => options.ValidateConsumer();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaTransportOptions.GroupId));
    }

    [Fact]
    public void ValidateConsumer_WhenGroupIdProvided_DoesNotThrow()
    {
        // Arrange
        var options = new KafkaTransportOptions
        {
            BootstrapServers = "localhost:9092",
            GroupId = "my-service-group",
        };

        // Act
        Action act = () => options.ValidateConsumer();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void DefaultValues_ConsumerOptions_AreConfluentRecommended()
    {
        // Arrange & Act
        var options = new KafkaTransportOptions();

        // Assert — Confluent-recommended at-least-once commit pattern (D6):
        // EnableAutoOffsetStore=false + EnableAutoCommit=true, offsets stored manually on Ack.
        options.EnableAutoOffsetStore.Should().BeFalse();
        options.EnableAutoCommit.Should().BeTrue();
        options.AutoOffsetReset.Should().Be(AutoOffsetReset.Earliest);
        options.ConsumerPartitionAssignmentStrategy.Should().Be(KafkaPartitionAssignmentStrategy.CooperativeSticky);
        options.SessionTimeoutMs.Should().BeNull();
        options.MaxPollIntervalMs.Should().BeNull();
    }
}
