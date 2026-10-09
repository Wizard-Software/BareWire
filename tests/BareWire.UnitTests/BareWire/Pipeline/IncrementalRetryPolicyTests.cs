using AwesomeAssertions;
using BareWire.Pipeline.Retry;

namespace BareWire.UnitTests.Core.Pipeline;

public sealed class IncrementalRetryPolicyTests
{
    // MUT-897, MUT-898, MUT-899: negative initial must throw ArgumentOutOfRangeException.
    // Kills MUT-897 (negated comparison would not throw for -1s),
    //        MUT-898 (fully negated guard would not throw),
    //        MUT-899 (removed throw would not throw).
    [Fact]
    public void Constructor_WhenInitialIsNegative_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        TimeSpan negativeInitial = TimeSpan.FromSeconds(-1);

        // Act
        Action act = () => _ = new IncrementalRetryPolicy(
            maxRetries: 3,
            initial: negativeInitial,
            increment: TimeSpan.FromSeconds(1),
            handledExceptions: [],
            ignoredExceptions: []);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("initial");
    }

    // MUT-896: boundary — TimeSpan.Zero must be accepted (guard is strict <, not <=).
    // Kills MUT-896 (mutated <= would reject zero and throw, but original code accepts it).
    [Fact]
    public void Constructor_WhenInitialIsZero_DoesNotThrow()
    {
        // Arrange & Act
        Action act = () => _ = new IncrementalRetryPolicy(
            maxRetries: 3,
            initial: TimeSpan.Zero,
            increment: TimeSpan.FromSeconds(1),
            handledExceptions: [],
            ignoredExceptions: []);

        // Assert
        act.Should().NotThrow();
    }

    // MUT-903: arithmetic mutation * → / in ticks calculation.
    // With attempt=0 both operators produce the same result (increment * 0 == increment / 0 would overflow,
    // but we use attempt=1 with a positive non-unit increment so * and / produce distinct values.
    // initial=0, increment=6s, attempt=1:
    //   original: 0 + 6s_ticks * 1 = 6s
    //   mutated:  0 + 6s_ticks / 1 = 6s  (same — bad choice, need attempt > 1)
    // Use attempt=2:
    //   original: 0 + 6s_ticks * 2 = 12s
    //   mutated:  0 + 6s_ticks / 2 = 3s  (different — mutant killed)
    [Fact]
    public void GetDelay_WhenRetryCountIsNonZeroWithNonZeroIncrement_ReturnsCorrectIncrementalDelay()
    {
        // Arrange
        TimeSpan initial = TimeSpan.Zero;
        TimeSpan increment = TimeSpan.FromSeconds(6);
        var policy = new IncrementalRetryPolicy(
            maxRetries: 5,
            initial: initial,
            increment: increment,
            handledExceptions: [],
            ignoredExceptions: []);

        // Act — attempt=2 means second retry (0-based)
        TimeSpan result = policy.GetDelay(attempt: 2);

        // Assert: 0 + 6s * 2 = 12s (mutant * → / would yield 3s)
        result.Should().Be(TimeSpan.FromSeconds(12));
    }

    private static IncrementalRetryPolicy Create(int maxRetries, TimeSpan initial, TimeSpan increment) =>
        new(maxRetries, initial, increment, [], []);

    [Fact]
    public void Constructor_WhenInitialExceedsOneHourByOneTick_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => _ = Create(3, TimeSpan.FromHours(1) + TimeSpan.FromTicks(1), TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("initial");
    }

    [Fact]
    public void Constructor_WhenInitialEqualsOneHour_DoesNotThrow()
    {
        Action act = () => _ = Create(3, TimeSpan.FromHours(1), TimeSpan.Zero);

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_WhenLargestComputedDelayExceedsOneHour_ThrowsArgumentOutOfRangeException()
    {
        // 99 * 37 s = 3663 s > 1 h
        Action act = () => _ = Create(100, TimeSpan.Zero, TimeSpan.FromSeconds(37));

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("increment");
    }

    [Fact]
    public void Constructor_WhenIncrementIsHugeAndMaxRetriesIsLarge_ThrowsInsteadOfOverflowing()
    {
        Action act = () => _ = Create(100, TimeSpan.Zero, TimeSpan.MaxValue);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("increment");
    }

    [Fact]
    public void Constructor_WhenLargestComputedDelayEqualsOneHour_DoesNotThrow()
    {
        // last delay (attempt 1) = 30 min + 30 min = exactly 1 h
        Action act = () => _ = Create(2, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

        act.Should().NotThrow();
    }

    [Fact]
    public void GetDelay_WhenAttemptIsExtreme_SaturatesAtMaxDelayWithoutOverflow()
    {
        IncrementalRetryPolicy policy = Create(2, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

        policy.GetDelay(int.MaxValue).Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void GetDelay_WhenIncrementIsNegative_ReturnsZeroInsteadOfNegative()
    {
        // Characterization of existing behaviour (passes before the fix).
        IncrementalRetryPolicy policy = Create(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-1));

        policy.GetDelay(3).Should().Be(TimeSpan.Zero);
    }
}
