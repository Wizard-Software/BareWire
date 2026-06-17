using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.AzureServiceBus.Topology;
using Xunit;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

public sealed class AzureServiceBusTopologyArgumentsTests
{
    private static QueueDeclaration QueueWithArgs(IReadOnlyDictionary<string, object>? arguments) =>
        new("test-queue", Durable: true, Exclusive: false, AutoDelete: false, Arguments: arguments);

    private static QueueDeclaration QueueWithNoArgs() =>
        new("test-queue", Durable: true, Exclusive: false, AutoDelete: false, Arguments: null);

    // ── Defaults ──────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_WithNoArguments_ReturnsDefaults()
    {
        // Arrange
        QueueDeclaration queue = QueueWithNoArgs();

        // Act
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        spec.MaxDeliveryCount.Should().Be(10);
        spec.LockDuration.Should().Be(TimeSpan.FromSeconds(30));
        spec.RequiresDuplicateDetection.Should().BeFalse();
    }

    [Fact]
    public void Parse_WithEmptyArguments_ReturnsDefaults()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>());

        // Act
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        spec.MaxDeliveryCount.Should().Be(10);
        spec.LockDuration.Should().Be(TimeSpan.FromSeconds(30));
        spec.RequiresDuplicateDetection.Should().BeFalse();
    }

    // ── MaxDeliveryCount ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_WithMaxDeliveryCount_SetsValue()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.MaxDeliveryCount] = 5,
        });

        // Act
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        spec.MaxDeliveryCount.Should().Be(5);
    }

    [Fact]
    public void Parse_WithMaxDeliveryCountAsString_ParsesCorrectly()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.MaxDeliveryCount] = "20",
        });

        // Act
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        spec.MaxDeliveryCount.Should().Be(20);
    }

    [Fact]
    public void Parse_WithInvalidNumeric_ThrowsConfigurationException()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.MaxDeliveryCount] = "not-a-number",
        });

        // Act
        Action act = () => AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(AzureServiceBusTopologyArguments.MaxDeliveryCount);
    }

    [Fact]
    public void Parse_WithMaxDeliveryCountZero_ThrowsConfigurationException()
    {
        // Arrange — max-delivery-count must be >= 1
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.MaxDeliveryCount] = 0,
        });

        // Act
        Action act = () => AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(AzureServiceBusTopologyArguments.MaxDeliveryCount);
    }

    // ── LockDuration ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_WithLockDurationTimeSpan_SetsValue()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.LockDuration] = TimeSpan.FromMinutes(5),
        });

        // Act
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        spec.LockDuration.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Parse_WithLockDurationString_ParsesCorrectly()
    {
        // Arrange — "00:02:00" = 2 minutes
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.LockDuration] = "00:02:00",
        });

        // Act
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        spec.LockDuration.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Parse_WithInvalidLockDuration_ThrowsConfigurationException()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.LockDuration] = "not-a-duration",
        });

        // Act
        Action act = () => AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(AzureServiceBusTopologyArguments.LockDuration);
    }

    // ── RequiresDuplicateDetection ────────────────────────────────────────────

    [Fact]
    public void Parse_WithRequiresDuplicateDetectionTrue_SetsValue()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.RequiresDuplicateDetection] = true,
        });

        // Act
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        spec.RequiresDuplicateDetection.Should().BeTrue();
    }

    [Fact]
    public void Parse_WithRequiresDuplicateDetectionAsString_ParsesCorrectly()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [AzureServiceBusTopologyArguments.RequiresDuplicateDetection] = "true",
        });

        // Act
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert
        spec.RequiresDuplicateDetection.Should().BeTrue();
    }

    // ── Unknown keys ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_WithUnknownKey_IsIgnoredSilently()
    {
        // Arrange — unknown keys should be forward-compatible (ignored)
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            ["bw.asb.future-feature"] = "some-value",
        });

        // Act — must not throw
        AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

        // Assert — defaults still returned
        spec.MaxDeliveryCount.Should().Be(10);
    }
}
