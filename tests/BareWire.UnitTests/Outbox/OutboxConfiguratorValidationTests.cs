using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Outbox;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;

namespace BareWire.UnitTests.Outbox;

/// <summary>
/// Unit tests for <see cref="OutboxOptions"/> validation and <see cref="IOutboxConfigurator"/>
/// covering the <c>OutboxLockTimeout</c> option added in B4.
/// </summary>
public sealed class OutboxConfiguratorValidationTests
{
    // ── OutboxLockTimeout > TimeSpan.Zero ────────────────────────────────────

    [Fact]
    public void OutboxOptions_WhenLockTimeoutIsZero_Throws()
    {
        // Arrange
        var options = new OutboxOptions
        {
            OutboxLockTimeout = TimeSpan.Zero,
            OutboxRetention = TimeSpan.FromDays(7),
            PollingInterval = TimeSpan.FromSeconds(1)
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage("*OutboxLockTimeout*greater than zero*");
    }

    [Fact]
    public void OutboxOptions_WhenLockTimeoutIsNegative_Throws()
    {
        // Arrange
        var options = new OutboxOptions
        {
            OutboxLockTimeout = TimeSpan.FromSeconds(-1),
            OutboxRetention = TimeSpan.FromDays(7),
            PollingInterval = TimeSpan.FromSeconds(1)
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage("*OutboxLockTimeout*greater than zero*");
    }

    // ── OutboxRetention > OutboxLockTimeout ──────────────────────────────────

    [Fact]
    public void OutboxOptions_WhenRetentionEqualsLockTimeout_Throws()
    {
        // Arrange
        var options = new OutboxOptions
        {
            OutboxLockTimeout = TimeSpan.FromHours(1),
            OutboxRetention = TimeSpan.FromHours(1), // equal — must fail
            PollingInterval = TimeSpan.FromSeconds(1)
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage("*OutboxRetention*OutboxLockTimeout*");
    }

    [Fact]
    public void OutboxOptions_WhenRetentionLessThanLockTimeout_Throws()
    {
        // Arrange
        var options = new OutboxOptions
        {
            OutboxLockTimeout = TimeSpan.FromDays(2),
            OutboxRetention = TimeSpan.FromDays(1), // less than lock — must fail
            PollingInterval = TimeSpan.FromSeconds(1)
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage("*OutboxRetention*OutboxLockTimeout*");
    }

    // ── OutboxLockTimeout >= 3 * PollingInterval ─────────────────────────────

    [Fact]
    public void OutboxOptions_WhenLockTimeoutLessThanThreeTimesPollingInterval_Throws()
    {
        // Arrange: PollingInterval = 10s, LockTimeout = 29s < 30s (3 * 10s)
        var options = new OutboxOptions
        {
            PollingInterval = TimeSpan.FromSeconds(10),
            OutboxLockTimeout = TimeSpan.FromSeconds(29),
            OutboxRetention = TimeSpan.FromDays(7)
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage("*OutboxLockTimeout*3 * PollingInterval*");
    }

    [Fact]
    public void OutboxOptions_WhenLockTimeoutEqualsThreeTimesPollingInterval_DoesNotThrow()
    {
        // Arrange: PollingInterval = 10s, LockTimeout = 30s = 3 * 10s — boundary is inclusive
        var options = new OutboxOptions
        {
            PollingInterval = TimeSpan.FromSeconds(10),
            OutboxLockTimeout = TimeSpan.FromSeconds(30),
            OutboxRetention = TimeSpan.FromDays(7),
            InboxRetention = TimeSpan.FromDays(8),
            InboxLockTimeout = TimeSpan.FromSeconds(29),
            CleanupInterval = TimeSpan.FromHours(1)
        };

        // Act
        Action act = () => options.Validate();

        // Assert — exactly 3 * PollingInterval must be allowed
        act.Should().NotThrow();
    }

    // ── Valid configuration (smoke — confirms defaults pass) ────────────────

    [Fact]
    public void OutboxOptions_DefaultConfiguration_DoesNotThrow()
    {
        // Arrange
        OutboxOptions options = OutboxOptions.Default;

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().NotThrow();
    }

    // ── OutboxConfigurator round-trip ────────────────────────────────────────

    [Fact]
    public void OutboxConfigurator_SetOutboxLockTimeout_PersistedInBuiltOptions()
    {
        // Arrange
        var expected = TimeSpan.FromSeconds(60);
        var configurator = new OutboxConfigurator();

        // Set via the interface to verify the property is on IOutboxConfigurator.
        ((IOutboxConfigurator)configurator).OutboxLockTimeout = expected;
        OutboxOptions options = configurator.Build();

        // Assert
        options.OutboxLockTimeout.Should().Be(expected);
    }
}
