using AwesomeAssertions;
using BareWire.Transport.Kafka.Internal;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class ExponentialBackoffCalculatorTests
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    [Fact]
    public void ForAttempt_FirstAttempt_ReturnsBaseDelay()
    {
        // Act
        TimeSpan delay = ExponentialBackoffCalculator.ForAttempt(1, BaseDelay, multiplier: 2.0, MaxDelay);

        // Assert — attempt 1 = baseDelay * 2^0 = baseDelay
        delay.Should().Be(BaseDelay);
    }

    [Fact]
    public void ForAttempt_SecondAttempt_ReturnsBaseTimesMultiplier()
    {
        // Act
        TimeSpan delay = ExponentialBackoffCalculator.ForAttempt(2, BaseDelay, multiplier: 2.0, MaxDelay);

        // Assert — attempt 2 = baseDelay * 2^1 = 2s
        delay.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ForAttempt_ThirdAttempt_ReturnsExponentialGrowth()
    {
        // Act
        TimeSpan delay = ExponentialBackoffCalculator.ForAttempt(3, BaseDelay, multiplier: 2.0, MaxDelay);

        // Assert — attempt 3 = baseDelay * 2^2 = 4s
        delay.Should().Be(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void ForAttempt_ExceedsMaxDelay_CapsAtMaxDelay()
    {
        // Arrange — attempt 10 with multiplier 2 = 512s, far above the 5s max
        var smallMax = TimeSpan.FromSeconds(5);

        // Act
        TimeSpan delay = ExponentialBackoffCalculator.ForAttempt(10, BaseDelay, multiplier: 2.0, smallMax);

        // Assert
        delay.Should().Be(smallMax);
    }

    [Fact]
    public void ForAttempt_HugeAttemptCausingOverflow_CapsAtMaxDelay()
    {
        // Arrange — PERF-2: Math.Pow(10, 63) → +Infinity; Math.Min(Infinity, max) must return max.
        var smallMax = TimeSpan.FromSeconds(30);

        // Act
        TimeSpan delay = ExponentialBackoffCalculator.ForAttempt(64, BaseDelay, multiplier: 10.0, smallMax);

        // Assert — overflow-safe: capped, not Infinity/overflow exception
        delay.Should().Be(smallMax);
    }

    [Fact]
    public void ForAttempt_MultiplierOne_ReturnsConstantBaseDelay()
    {
        // Act — multiplier 1 means no growth: every attempt == baseDelay
        TimeSpan delay2 = ExponentialBackoffCalculator.ForAttempt(2, BaseDelay, multiplier: 1.0, MaxDelay);
        TimeSpan delay5 = ExponentialBackoffCalculator.ForAttempt(5, BaseDelay, multiplier: 1.0, MaxDelay);

        // Assert
        delay2.Should().Be(BaseDelay);
        delay5.Should().Be(BaseDelay);
    }

    [Fact]
    public void ForAttempt_AttemptZero_ThrowsArgumentOutOfRangeException()
    {
        // Act
        Action act = () => ExponentialBackoffCalculator.ForAttempt(0, BaseDelay, multiplier: 2.0, MaxDelay);

        // Assert — attempts are 1-based
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("attempt");
    }

    [Fact]
    public void ForAttempt_NegativeAttempt_ThrowsArgumentOutOfRangeException()
    {
        // Act
        Action act = () => ExponentialBackoffCalculator.ForAttempt(-1, BaseDelay, multiplier: 2.0, MaxDelay);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("attempt");
    }
}
