using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqTransportAdapterTests
{
    private static readonly string ValidConnectionString = "amqp://guest:guest@localhost:5672/";

    private static NullLogger<RabbitMqTransportAdapter> CreateLogger() =>
        NullLogger<RabbitMqTransportAdapter>.Instance;

    private static RabbitMqTransportOptions ValidOptions() =>
        new() { ConnectionString = ValidConnectionString };

    // ── TransportName ─────────────────────────────────────────────────────────

    [Fact]
    public void TransportName_ReturnsRabbitMQ()
    {
        // Arrange
        var adapter = new RabbitMqTransportAdapter(ValidOptions(), CreateLogger());

        // Act
        string name = adapter.TransportName;

        // Assert
        name.Should().Be("RabbitMQ");
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_ReturnsExpectedFlags()
    {
        // Arrange
        var adapter = new RabbitMqTransportAdapter(ValidOptions(), CreateLogger());

        // Act
        TransportCapabilities caps = adapter.Capabilities;

        // Assert — ADR-006: publisher confirms + native DLQ + flow control
        caps.Should().HaveFlag(TransportCapabilities.PublisherConfirms);
        caps.Should().HaveFlag(TransportCapabilities.DlqNative);
        caps.Should().HaveFlag(TransportCapabilities.FlowControl);
    }

    // ── Constructor guards ────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        // Arrange
        ILogger<RabbitMqTransportAdapter> logger = CreateLogger();

        // Act
        Action act = () => _ = new RabbitMqTransportAdapter(null!, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("options");
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Arrange
        RabbitMqTransportOptions options = ValidOptions();

        // Act
        Action act = () => _ = new RabbitMqTransportAdapter(options, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    public void Constructor_InvalidOptions_ThrowsBareWireConfigurationException()
    {
        // Arrange — empty connection string triggers Validate() failure
        var options = new RabbitMqTransportOptions { ConnectionString = string.Empty };
        ILogger<RabbitMqTransportAdapter> logger = CreateLogger();

        // Act
        Action act = () => _ = new RabbitMqTransportAdapter(options, logger);

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(RabbitMqTransportOptions.ConnectionString));
    }

    [Fact]
    public void Constructor_ValidOptions_DoesNotThrow()
    {
        // Arrange
        RabbitMqTransportOptions options = ValidOptions();
        ILogger<RabbitMqTransportAdapter> logger = CreateLogger();

        // Act
        Action act = () => _ = new RabbitMqTransportAdapter(options, logger);

        // Assert
        act.Should().NotThrow();
    }
}
