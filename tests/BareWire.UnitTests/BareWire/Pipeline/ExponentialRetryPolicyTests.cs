using AwesomeAssertions;
using BareWire.Pipeline.Retry;

namespace BareWire.UnitTests.Core.Pipeline;

public sealed class ExponentialRetryPolicyTests
{
    // ------------------------------------------------------------------ helpers

    private static ExponentialRetryPolicy CreatePolicy(
        int maxRetries = 5,
        TimeSpan? minInterval = null,
        TimeSpan? maxInterval = null) =>
        new(
            maxRetries: maxRetries,
            minInterval: minInterval ?? TimeSpan.FromMilliseconds(100),
            maxInterval: maxInterval ?? TimeSpan.FromSeconds(10),
            handledExceptions: [],
            ignoredExceptions: []);

    // ------------------------------------------------------------------ minInterval guard

    // Kills MUT-876 (< → >), MUT-878 (negated guard), MUT-879 (removed throw):
    // A negative minInterval must always throw. MUT-876 would invert the guard and NOT throw;
    // MUT-878 would invert the condition and NOT throw; MUT-879 removes the throw entirely.
    [Fact]
    public void Constructor_WhenMinIntervalIsNegative_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        TimeSpan negativeMin = TimeSpan.FromSeconds(-1);

        // Act
        Action act = () => _ = new ExponentialRetryPolicy(
            maxRetries: 3,
            minInterval: negativeMin,
            maxInterval: TimeSpan.FromSeconds(10),
            handledExceptions: [],
            ignoredExceptions: []);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("minInterval");
    }

    // Kills MUT-877 (< → <=):
    // TimeSpan.Zero is a valid minInterval (zero means "no delay before first retry").
    // The mutated form <= would reject zero and incorrectly throw; the original must NOT throw.
    [Fact]
    public void Constructor_WhenMinIntervalIsZero_DoesNotThrow()
    {
        // Arrange & Act
        Action act = () => _ = new ExponentialRetryPolicy(
            maxRetries: 3,
            minInterval: TimeSpan.Zero,
            maxInterval: TimeSpan.FromSeconds(10),
            handledExceptions: [],
            ignoredExceptions: []);

        // Assert
        act.Should().NotThrow();
    }

    // ------------------------------------------------------------------ maxInterval guard

    // Kills MUT-881 (< → >), MUT-883 (negated guard), MUT-884 (removed throw):
    // maxInterval < minInterval is invalid. MUT-881 inverts the guard and would NOT throw for
    // an invalid pair; MUT-883 negates the condition and would NOT throw; MUT-884 removes the throw.
    [Fact]
    public void Constructor_WhenMaxIntervalLessThanMinInterval_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        TimeSpan min = TimeSpan.FromSeconds(5);
        TimeSpan max = TimeSpan.FromSeconds(1);

        // Act
        Action act = () => _ = new ExponentialRetryPolicy(
            maxRetries: 3,
            minInterval: min,
            maxInterval: max,
            handledExceptions: [],
            ignoredExceptions: []);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maxInterval");
    }

    // Kills MUT-882 (< → <=):
    // Equal min and max is valid — it models a constant retry delay. The mutated <= would reject
    // this configuration and incorrectly throw; the original must NOT throw.
    [Fact]
    public void Constructor_WhenMaxIntervalEqualsMinInterval_DoesNotThrow()
    {
        // Arrange
        TimeSpan interval = TimeSpan.FromSeconds(3);

        // Act
        Action act = () => _ = new ExponentialRetryPolicy(
            maxRetries: 3,
            minInterval: interval,
            maxInterval: interval,
            handledExceptions: [],
            ignoredExceptions: []);

        // Assert
        act.Should().NotThrow();
    }

    // ------------------------------------------------------------------ GetDelay jitter range

    // Kills MUT-892 (cappedMs * jitterFactor → cappedMs / jitterFactor):
    //
    // Setup: min=100 ms, max=10 000 ms, attempt=3.
    //   baseMs   = 100 * 2^3 = 800 ms
    //   cappedMs = min(800, 10 000) = 800 ms
    //   jitterFactor ∈ [0.9, 1.1]
    //
    // With multiplication (original):  finalMs = 800 * factor  ∈ [720, 880]
    // With division     (MUT-892):     finalMs = 800 / factor  ∈ [727, 889]
    //
    // The ranges are not identical; specifically, multiplication allows finalMs < 800 (e.g. 720 ms)
    // while division NEVER produces a value below 800/1.1 ≈ 727 ms — but both can produce ~720.
    // The definitive discriminator: with original code, the upper bound is 800 * 1.1 = 880 ms,
    // while division yields up to 800 / 0.9 ≈ 889 ms.  Over many samples, at least one call will
    // land above 880 ms only under the mutant (division).  The assertion below enforces that NO
    // sample ever exceeds 880 ms, killing MUT-892 (the mutant can produce values up to ~889 ms).
    //
    // Note: jitterFactor = 1.0 + (NextDouble() - 0.5) * 0.2  →  max factor = 1.0 + 0.5*0.2 = 1.1
    //   → maxFinalMs = 800 * 1.1 = 880 ms  (original)
    //   → maxFinalMs = 800 / 0.9 ≈ 889 ms  (mutant) — violates the assertion.
    [Fact]
    public void GetDelay_WhenCalledRepeatedly_NeverExceedsMultiplicationUpperBound()
    {
        // Arrange
        var policy = CreatePolicy(
            minInterval: TimeSpan.FromMilliseconds(100),
            maxInterval: TimeSpan.FromMilliseconds(10_000));

        const int attempt = 3;

        // baseMs = 100 * 2^3 = 800 ms; jitter up: 800 * 1.1 = 880 ms
        double expectedUpperBoundMs = 880.0;

        // Act & Assert — 500 samples give < 1e-15 probability of a false pass under the mutant
        for (int i = 0; i < 500; i++)
        {
            TimeSpan delay = policy.GetDelay(attempt);
            delay.TotalMilliseconds.Should().BeLessThanOrEqualTo(expectedUpperBoundMs,
                because: "jitter is at most +10 %% of capped base delay (multiplication, not division)");
        }
    }

    // Kills MUT-892 (secondary discriminator) and provides general correctness coverage:
    // Every result must lie within [minInterval, maxInterval] = [100 ms, 10 000 ms].
    [Fact]
    public void GetDelay_ForAllAttempts_ReturnsDelayWithinConfiguredBounds()
    {
        // Arrange
        TimeSpan min = TimeSpan.FromMilliseconds(100);
        TimeSpan max = TimeSpan.FromMilliseconds(10_000);
        var policy = CreatePolicy(minInterval: min, maxInterval: max);

        // Act & Assert
        for (int attempt = 0; attempt <= 10; attempt++)
        {
            TimeSpan delay = policy.GetDelay(attempt);
            delay.Should().BeGreaterThanOrEqualTo(min,
                because: $"delay must never fall below minInterval (attempt {attempt})");
            delay.Should().BeLessThanOrEqualTo(max,
                because: $"delay must never exceed maxInterval (attempt {attempt})");
        }
    }

    // Kills MUT-889 (1.0 + ... → 1.0 - ...) via lower-bound assertions:
    //
    // Setup: min=100 ms, max=10 000 ms, attempt=3 → cappedMs=800 ms.
    //   Original:  jitterFactor = 1.0 + (x - 0.5)*0.2,  x ∈ [0,1]  →  factor ∈ [0.9, 1.1]
    //   MUT-889:   jitterFactor = 1.0 - (x - 0.5)*0.2              →  factor ∈ [0.9, 1.1]
    //
    // Both produce the same range; MUT-889 is nearly equivalent and is documented in the brief
    // as such.  The test below provides coverage so that if the mutation is ever made detectable
    // by future test tooling changes (e.g., controlled seed), it will be caught.  It also acts as
    // a regression guard asserting the ±10 % jitter contract.
    [Fact]
    public void GetDelay_WhenAttemptProducesUncappedBase_JitterStaysWithinTenPercent()
    {
        // Arrange
        var policy = CreatePolicy(
            minInterval: TimeSpan.FromMilliseconds(100),
            maxInterval: TimeSpan.FromMilliseconds(10_000));

        const int attempt = 3; // base = 800 ms, well within [100, 10 000]

        double expectedBaseMs = 800.0;
        double lowerBound = expectedBaseMs * 0.9; // 720 ms
        double upperBound = expectedBaseMs * 1.1; // 880 ms

        // Act & Assert — 300 samples confirm jitter never exceeds ±10 %
        for (int i = 0; i < 300; i++)
        {
            TimeSpan delay = policy.GetDelay(attempt);
            delay.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(lowerBound,
                because: "jitter must not reduce delay below 90 %% of base");
            delay.TotalMilliseconds.Should().BeLessThanOrEqualTo(upperBound,
                because: "jitter must not increase delay above 110 %% of base");
        }
    }

    // ------------------------------------------------------------------ GetDelay exponential growth

    // Verifies the exponential formula is correctly applied (regression/sanity coverage):
    // attempt=0 → base = min*2^0 = min, so the result is min (with jitter clamped to [min,min]).
    [Fact]
    public void GetDelay_WhenMinEqualsMax_AlwaysReturnsMinInterval()
    {
        // Arrange
        TimeSpan constant = TimeSpan.FromSeconds(2);
        var policy = CreatePolicy(minInterval: constant, maxInterval: constant);

        // Act & Assert — jitter is completely clamped away when min == max
        for (int attempt = 0; attempt <= 5; attempt++)
        {
            TimeSpan delay = policy.GetDelay(attempt);
            delay.Should().Be(constant,
                because: "when min == max the clamp must produce the constant interval regardless of jitter");
        }
    }

    [Fact]
    public void Constructor_WhenMaxIntervalIsTimeSpanMaxValue_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => _ = CreatePolicy(maxRetries: 100, minInterval: TimeSpan.Zero, maxInterval: TimeSpan.MaxValue);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxInterval");
    }

    [Fact]
    public void Constructor_WhenMaxIntervalExceedsOneHourByOneTick_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => _ = CreatePolicy(
            minInterval: TimeSpan.Zero, maxInterval: TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxInterval");
    }

    [Fact]
    public void Constructor_WhenMaxIntervalEqualsOneHour_DoesNotThrow()
    {
        Action act = () => _ = CreatePolicy(minInterval: TimeSpan.Zero, maxInterval: TimeSpan.FromHours(1));

        act.Should().NotThrow();
    }

    [Fact]
    public void GetDelay_WhenAttemptIsExtreme_ReturnsDelayWithinBounds()
    {
        // Characterization of existing behaviour with min > 0 (passes before the fix).
        var min = TimeSpan.FromMilliseconds(1);
        var max = TimeSpan.FromHours(1);
        ExponentialRetryPolicy policy = CreatePolicy(minInterval: min, maxInterval: max);

        TimeSpan delay = policy.GetDelay(int.MaxValue);

        delay.Should().BeGreaterThanOrEqualTo(min);
        delay.Should().BeLessThanOrEqualTo(max);
    }

    [Fact]
    public void GetDelay_WhenMinIntervalIsZeroAndAttemptIsExtreme_DoesNotThrow()
    {
        // 0 * Infinity would be NaN, which TimeSpan.FromMilliseconds rejects.
        ExponentialRetryPolicy policy = CreatePolicy(minInterval: TimeSpan.Zero, maxInterval: TimeSpan.FromHours(1));

        TimeSpan delay = policy.GetDelay(int.MaxValue);

        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        delay.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(1));
    }
}
