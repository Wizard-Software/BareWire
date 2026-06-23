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

    // ── OrderingMode.PerKey validation (U1) ──────────────────────────────────

    [Fact]
    public void OutboxOptions_WhenPerKeyAndHeaderNameIsNull_Throws()
    {
        // Arrange
        var options = new OutboxOptions
        {
            OrderingMode = OrderingMode.PerKey,
            OrderingKeyHeaderName = null
        };

        // Act
        Action act = () => options.Validate();

        // Assert — must throw; message must NOT contain any header value
        var ex = act.Should().ThrowExactly<BareWireConfigurationException>().Which;
        ex.Message.Should().Contain("OrderingKeyHeaderName");
        ex.Message.Should().Contain("PerKey");
        // The null value must not appear as a literal in the message.
        ex.Message.Should().NotContain("null");
    }

    [Fact]
    public void OutboxOptions_WhenPerKeyAndHeaderNameIsEmpty_Throws()
    {
        // Arrange
        var options = new OutboxOptions
        {
            OrderingMode = OrderingMode.PerKey,
            OrderingKeyHeaderName = string.Empty
        };

        // Act
        Action act = () => options.Validate();

        // Assert — must throw; message must not echo the empty-string value
        var ex = act.Should().ThrowExactly<BareWireConfigurationException>().Which;
        ex.Message.Should().Contain("OrderingKeyHeaderName");
        // The supplied empty value must not appear as its own token in the message.
        ex.Message.Should().NotBe(string.Empty);
    }

    [Fact]
    public void OutboxOptions_WhenPerKeyAndHeaderNameIsWhitespace_Throws()
    {
        // Arrange
        var options = new OutboxOptions
        {
            OrderingMode = OrderingMode.PerKey,
            OrderingKeyHeaderName = "   "
        };

        // Act
        Action act = () => options.Validate();

        // Assert — must throw; message must NOT echo the whitespace value
        var ex = act.Should().ThrowExactly<BareWireConfigurationException>().Which;
        ex.Message.Should().Contain("OrderingKeyHeaderName");
        ex.Message.Should().NotContain("   ");
    }

    // ── OrderingMode happy paths (U2) ─────────────────────────────────────────

    [Fact]
    public void OutboxOptions_WhenPerKeyAndHeaderNameIsNonEmpty_DoesNotThrow()
    {
        // Arrange — PerKey with a valid header name must be accepted
        var options = new OutboxOptions
        {
            OrderingMode = OrderingMode.PerKey,
            OrderingKeyHeaderName = "x-aggregate-id"
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void OutboxOptions_WhenNoneWithoutHeaderName_DoesNotThrow()
    {
        // Arrange — None mode with no header name must not require the header name
        var options = new OutboxOptions
        {
            OrderingMode = OrderingMode.None,
            OrderingKeyHeaderName = null
        };

        // Act
        Action act = () => options.Validate();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void OutboxConfigurator_SetOrderingModePerKey_PersistedInBuiltOptions()
    {
        // Arrange
        var configurator = new OutboxConfigurator();

        // Set via the interface to verify both properties are on IOutboxConfigurator.
        ((IOutboxConfigurator)configurator).OrderingMode = OrderingMode.PerKey;
        ((IOutboxConfigurator)configurator).OrderingKeyHeaderName = "x-order-id";

        // Act
        OutboxOptions options = configurator.Build();

        // Assert — both properties must round-trip through Build()
        options.OrderingMode.Should().Be(OrderingMode.PerKey);
        options.OrderingKeyHeaderName.Should().Be("x-order-id");
    }

    [Fact]
    public void OutboxConfigurator_DefaultOrderingMode_IsNone()
    {
        // Arrange & Act — default configurator must default to None
        var options = new OutboxConfigurator().Build();

        // Assert
        options.OrderingMode.Should().Be(OrderingMode.None);
        options.OrderingKeyHeaderName.Should().BeNull();
    }
}
