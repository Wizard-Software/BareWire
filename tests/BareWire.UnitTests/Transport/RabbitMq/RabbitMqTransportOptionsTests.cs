using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.RabbitMQ;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqTransportOptionsTests
{
    // ── Validate ──────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_NullConnectionString_ThrowsBareWireConfigurationException()
    {
        // Arrange
        var options = new RabbitMqTransportOptions { ConnectionString = null! };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(RabbitMqTransportOptions.ConnectionString));
    }

    [Fact]
    public void Validate_EmptyConnectionString_ThrowsBareWireConfigurationException()
    {
        // Arrange
        var options = new RabbitMqTransportOptions { ConnectionString = string.Empty };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(RabbitMqTransportOptions.ConnectionString));
    }

    [Fact]
    public void Validate_ValidConnectionString_DoesNotThrow()
    {
        // Arrange
        var options = new RabbitMqTransportOptions
        {
            ConnectionString = "amqp://guest:guest@localhost:5672/"
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().NotThrow();
    }

    // ── Default values ────────────────────────────────────────────────────────

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var options = new RabbitMqTransportOptions();

        // Assert — verify every documented default from the plan
        options.ConnectionString.Should().BeEmpty();
        options.AutomaticRecoveryEnabled.Should().BeTrue();
        options.NetworkRecoveryInterval.Should().Be(TimeSpan.FromSeconds(5));
        options.DefaultExchange.Should().BeEmpty();
        options.SslOptions.Should().BeNull();
        options.DeferEnabled.Should().BeFalse();
        options.DeferDelayMs.Should().Be(30_000);
    }

    [Theory]
    [InlineData("amqp://localhost")]
    [InlineData("amqp://guest:guest@localhost:5672/")]
    [InlineData("amqps://host:5671/vhost")]
    public void Validate_VariousValidConnectionStrings_DoNotThrow(string connectionString)
    {
        // Arrange
        var options = new RabbitMqTransportOptions { ConnectionString = connectionString };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().NotThrow();
    }

    // ── Recovery health timeout ───────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveRecoveryHealthTimeout_ThrowsBareWireConfigurationException(int seconds)
    {
        // Arrange
        var options = new RabbitMqTransportOptions
        {
            ConnectionString = "amqp://localhost",
            RecoveryHealthTimeout = TimeSpan.FromSeconds(seconds),
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(RabbitMqTransportOptions.RecoveryHealthTimeout));
    }

    [Theory]
    [InlineData(5, 30)]
    [InlineData(10, 30)]
    [InlineData(20, 60)]
    public void EffectiveRecoveryHealthTimeout_NotSet_IsThreeRecoveryIntervalsButAtLeast30Seconds(
        int intervalSeconds, int expectedSeconds)
    {
        // Arrange
        var options = new RabbitMqTransportOptions
        {
            NetworkRecoveryInterval = TimeSpan.FromSeconds(intervalSeconds),
        };

        // Act
        TimeSpan effective = options.EffectiveRecoveryHealthTimeout;

        // Assert
        effective.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void EffectiveRecoveryHealthTimeout_Set_UsesConfiguredValue()
    {
        // Arrange
        var options = new RabbitMqTransportOptions { RecoveryHealthTimeout = TimeSpan.FromSeconds(7) };

        // Act
        TimeSpan effective = options.EffectiveRecoveryHealthTimeout;

        // Assert
        effective.Should().Be(TimeSpan.FromSeconds(7));
    }
}
