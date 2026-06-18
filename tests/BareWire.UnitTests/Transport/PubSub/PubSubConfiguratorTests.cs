using AwesomeAssertions;
using BareWire.Transport.Google.PubSub;
using BareWire.Transport.Google.PubSub.Configuration;
using Xunit;

namespace BareWire.UnitTests.Transport.PubSub;

public sealed class PubSubConfiguratorTests
{
    // ── UseServiceAccountJson ─────────────────────────────────────────────────

    [Fact]
    public void UseServiceAccountJson_ValidPath_SetsServiceAccountJsonPathAndAuthMode()
    {
        // Arrange
        var configurator = new PubSubConfigurator();
        configurator.ProjectId("test-project");

        // Act
        configurator.UseServiceAccountJson("/etc/sa/key.json");
        PubSubTransportOptions options = configurator.Build();

        // Assert
        options.ServiceAccountJsonPath.Should().Be("/etc/sa/key.json");
        options.AuthMode.Should().Be(PubSubAuthMode.ServiceAccountJson);
    }

    // ── ProjectId ─────────────────────────────────────────────────────────────

    [Fact]
    public void ProjectId_ValidValue_SetsProjectIdOnOptions()
    {
        // Arrange
        var configurator = new PubSubConfigurator();

        // Act
        configurator.ProjectId("my-gcp-project");
        PubSubTransportOptions options = configurator.Build();

        // Assert
        options.ProjectId.Should().Be("my-gcp-project");
    }

    // ── AckDeadline ───────────────────────────────────────────────────────────

    [Fact]
    public void AckDeadline_ValidTimeSpan_SetsDefaultAckDeadlineOnOptions()
    {
        // Arrange
        var configurator = new PubSubConfigurator();
        configurator.ProjectId("test-project");
        var deadline = TimeSpan.FromSeconds(120);

        // Act
        configurator.AckDeadline(deadline);
        PubSubTransportOptions options = configurator.Build();

        // Assert
        options.DefaultAckDeadline.Should().Be(deadline);
    }

    // ── MaxOutstandingMessages ────────────────────────────────────────────────

    [Fact]
    public void MaxOutstandingMessages_ValidValue_SetsMaxOutstandingMessagesOnOptions()
    {
        // Arrange
        var configurator = new PubSubConfigurator();
        configurator.ProjectId("test-project");

        // Act
        configurator.MaxOutstandingMessages(250);
        PubSubTransportOptions options = configurator.Build();

        // Assert
        options.MaxOutstandingMessages.Should().Be(250);
    }

    // ── MaxOutstandingBytes ───────────────────────────────────────────────────

    [Fact]
    public void MaxOutstandingBytes_ValidValue_SetsMaxOutstandingBytesOnOptions()
    {
        // Arrange
        var configurator = new PubSubConfigurator();
        configurator.ProjectId("test-project");

        // Act
        configurator.MaxOutstandingBytes(16L * 1024 * 1024); // 16 MiB
        PubSubTransportOptions options = configurator.Build();

        // Assert
        options.MaxOutstandingBytes.Should().Be(16L * 1024 * 1024);
    }

    // ── MaxInFlightMessages ───────────────────────────────────────────────────

    [Fact]
    public void MaxInFlightMessages_ValidValue_SetsMaxInFlightMessagesOnOptions()
    {
        // Arrange
        var configurator = new PubSubConfigurator();
        configurator.ProjectId("test-project");

        // Act
        configurator.MaxInFlightMessages(50);
        PubSubTransportOptions options = configurator.Build();

        // Assert
        options.MaxInFlightMessages.Should().Be(50);
    }

    // ── EnableMessageOrdering ─────────────────────────────────────────────────

    [Fact]
    public void EnableMessageOrdering_Called_SetsEnableMessageOrderingTrueOnOptions()
    {
        // Arrange
        var configurator = new PubSubConfigurator();
        configurator.ProjectId("test-project");

        // Act
        configurator.EnableMessageOrdering();
        PubSubTransportOptions options = configurator.Build();

        // Assert
        options.EnableMessageOrdering.Should().BeTrue(
            "EnableMessageOrdering() must set EnableMessageOrdering = true on the built options");
    }

    // ── Default values — EnableMessageOrdering is off by default ─────────────

    [Fact]
    public void Build_WithoutCallingEnableMessageOrdering_LeavesEnableMessageOrderingFalse()
    {
        // Arrange
        var configurator = new PubSubConfigurator();
        configurator.ProjectId("test-project");

        // Act
        PubSubTransportOptions options = configurator.Build();

        // Assert
        options.EnableMessageOrdering.Should().BeFalse(
            "message ordering must default to false so topology is not silently changed");
    }
}
