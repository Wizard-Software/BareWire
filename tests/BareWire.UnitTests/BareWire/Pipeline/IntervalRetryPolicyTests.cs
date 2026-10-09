using AwesomeAssertions;
using BareWire.Pipeline.Retry;

namespace BareWire.UnitTests.Core.Pipeline;

public sealed class IntervalRetryPolicyTests
{
    // MUT-909, MUT-910, MUT-911: negative interval must throw ArgumentOutOfRangeException
    [Fact]
    public void Constructor_WhenIntervalIsNegative_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        TimeSpan negativeInterval = TimeSpan.FromSeconds(-1);

        // Act
        Action act = () => _ = new IntervalRetryPolicy(
            maxRetries: 3,
            interval: negativeInterval,
            handledExceptions: [],
            ignoredExceptions: []);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("interval");
    }

    // MUT-908: TimeSpan.Zero must be accepted (boundary — zero is valid, not negative)
    [Fact]
    public void Constructor_WhenIntervalIsZero_DoesNotThrow()
    {
        // Arrange & Act
        Action act = () => _ = new IntervalRetryPolicy(
            maxRetries: 3,
            interval: TimeSpan.Zero,
            handledExceptions: [],
            ignoredExceptions: []);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_WhenIntervalExceedsOneHourByOneTick_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => _ = new IntervalRetryPolicy(
            maxRetries: 3,
            interval: TimeSpan.FromHours(1) + TimeSpan.FromTicks(1),
            handledExceptions: [],
            ignoredExceptions: []);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("interval");
    }

    [Fact]
    public void Constructor_WhenIntervalEqualsOneHour_DoesNotThrow()
    {
        Action act = () => _ = new IntervalRetryPolicy(
            maxRetries: 3,
            interval: TimeSpan.FromHours(1),
            handledExceptions: [],
            ignoredExceptions: []);

        act.Should().NotThrow();
    }
}
