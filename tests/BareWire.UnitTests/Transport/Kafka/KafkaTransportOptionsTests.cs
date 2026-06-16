using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.Kafka;
using Confluent.Kafka;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaTransportOptionsTests
{
    // ── Validate ──────────────────────────────────────────────────────────────

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

    // ── Default values ────────────────────────────────────────────────────────

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
}
