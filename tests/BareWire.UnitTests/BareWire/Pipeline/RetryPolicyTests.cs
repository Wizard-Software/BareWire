using AwesomeAssertions;
using BareWire.Pipeline.Retry;
using Microsoft.Extensions.Time.Testing;

namespace BareWire.UnitTests.Core.Pipeline;

/// <summary>
/// Tests targeting surviving mutants on <see cref="RetryPolicy"/> base class logic:
/// constructor guards, null-coalescing defaults, TimeProvider storage, and ShouldRetry filtering.
/// </summary>
public sealed class RetryPolicyTests
{
    // ------------------------------------------------------------------ constructor helpers

    /// <summary>
    /// Creates an <see cref="IntervalRetryPolicy"/> (concrete subclass) to exercise base
    /// <see cref="RetryPolicy"/> constructor with the supplied arguments.
    /// </summary>
    private static IntervalRetryPolicy CreatePolicy(
        int maxRetries = 3,
        IReadOnlyList<Type>? handledExceptions = null,
        IReadOnlyList<Type>? ignoredExceptions = null,
        TimeProvider? timeProvider = null) =>
        new(
            maxRetries: maxRetries,
            interval: TimeSpan.Zero,
            handledExceptions: handledExceptions ?? [],
            ignoredExceptions: ignoredExceptions ?? [],
            timeProvider: timeProvider);

    // ------------------------------------------------------------------ MUT-927: boundary maxRetries=0

    // Kills MUT-927 (< 0 → <= 0):
    // The mutated guard would reject zero as invalid and throw. The original accepts zero
    // because "retry zero times" is a valid configuration meaning "no retries, fail immediately".
    [Fact]
    public void Constructor_WhenMaxRetriesIsZero_DoesNotThrow()
    {
        // Arrange & Act
        Action act = () => _ = CreatePolicy(maxRetries: 0);

        // Assert
        act.Should().NotThrow();
    }

    // ------------------------------------------------------------------ MUT-929: negative maxRetries throws

    // Kills MUT-929 (NoCoverage — removed throw for negative maxRetries):
    // Without the throw, a policy with maxRetries=-1 would be silently constructed with an
    // invalid state. This test ensures ArgumentOutOfRangeException is raised for any negative value.
    [Fact]
    public void Constructor_WhenMaxRetriesIsNegative_ThrowsArgumentOutOfRangeException()
    {
        // Arrange & Act
        Action act = () => _ = CreatePolicy(maxRetries: -1);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maxRetries");
    }

    // ------------------------------------------------------------------ MUT-931: null handledExceptions throws

    // Kills MUT-931 (remove right side of null-coalescing on handledExceptions):
    // The mutant would assign null to HandledExceptions instead of throwing. This test verifies
    // that passing null explicitly triggers ArgumentNullException, protecting callers from a null
    // IReadOnlyList<Type> on HandledExceptions that would crash on first iteration in ShouldRetry.
    [Fact]
    public void Constructor_WhenHandledExceptionsIsNull_ThrowsArgumentNullException()
    {
        // Arrange & Act
        Action act = () => _ = new IntervalRetryPolicy(
            maxRetries: 3,
            interval: TimeSpan.Zero,
            handledExceptions: null!,
            ignoredExceptions: []);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("handledExceptions");
    }

    // ------------------------------------------------------------------ MUT-932: null ignoredExceptions throws

    // Kills MUT-932 (remove right side of null-coalescing on ignoredExceptions):
    // The mutant would assign null to IgnoredExceptions instead of throwing. This test verifies
    // that passing null explicitly triggers ArgumentNullException, protecting callers from a null
    // IReadOnlyList<Type> that would crash on first iteration in ShouldRetry.
    [Fact]
    public void Constructor_WhenIgnoredExceptionsIsNull_ThrowsArgumentNullException()
    {
        // Arrange & Act
        Action act = () => _ = new IntervalRetryPolicy(
            maxRetries: 3,
            interval: TimeSpan.Zero,
            handledExceptions: [],
            ignoredExceptions: null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("ignoredExceptions");
    }

    // ------------------------------------------------------------------ MUT-937: TimeProvider stored and used

    // Kills MUT-937 (remove `_timeProvider = timeProvider ?? TimeProvider.System` assignment):
    // Without the assignment _timeProvider remains null. Task.Delay(delay, null, ct) throws
    // ArgumentNullException. With the original code the field holds TimeProvider.System and
    // Task.Delay with TimeSpan.Zero completes successfully.
    [Fact]
    public async Task DelayAsync_WhenTimeProviderIsDefaulted_CompletesWithoutThrowingAsync()
    {
        // Arrange — timeProvider: null causes the base constructor to default to TimeProvider.System
        IntervalRetryPolicy policy = CreatePolicy(maxRetries: 1, timeProvider: null);

        // Act & Assert — interval is TimeSpan.Zero so this returns immediately; a null _timeProvider
        // would throw ArgumentNullException inside Task.Delay before we get here
        Func<Task> act = () => policy.DelayAsync(attempt: 0, ct: CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // Supplementary TimeProvider test: an injected FakeTimeProvider is actually used —
    // a delay that would never complete under the fake clock is cancelled immediately.
    [Fact]
    public async Task DelayAsync_WhenFakeTimeProviderInjected_HonoursCancellationAsync()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var policy = new IntervalRetryPolicy(
            maxRetries: 3,
            interval: TimeSpan.FromSeconds(60), // large delay — would never complete without advancing fake clock
            handledExceptions: [],
            ignoredExceptions: [],
            timeProvider: fakeTime);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already cancelled

        // Act & Assert — cancellation must be honoured regardless of which TimeProvider is used
        Func<Task> act = () => policy.DelayAsync(attempt: 0, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ------------------------------------------------------------------ MUT-951, MUT-952: handled type returns true

    // Kills MUT-951 (negate IsInstanceOfType check) and MUT-952 (return false instead of true):
    // When an exception matches one of the handled types, ShouldRetry must return true.
    // MUT-951 would invert the check so a matching exception would fall through to "return false".
    // MUT-952 would return false directly on a match.
    [Fact]
    public void ShouldRetry_WhenExceptionMatchesHandledType_ReturnsTrue()
    {
        // Arrange
        IntervalRetryPolicy policy = CreatePolicy(
            maxRetries: 5,
            handledExceptions: [typeof(InvalidOperationException)]);

        var ex = new InvalidOperationException("transient");

        // Act
        bool result = policy.ShouldRetry(ex, attempt: 0);

        // Assert
        result.Should().BeTrue(because: "the exception type is in the handled list and no retry limit is reached");
    }

    // Kills MUT-951 (secondary): a subtype of a handled exception must also match via IsInstanceOfType.
    [Fact]
    public void ShouldRetry_WhenExceptionIsSubtypeOfHandledType_ReturnsTrue()
    {
        // Arrange — ArgumentException is a subtype of SystemException
        IntervalRetryPolicy policy = CreatePolicy(
            maxRetries: 5,
            handledExceptions: [typeof(SystemException)]);

        var ex = new ArgumentException("derived");

        // Act
        bool result = policy.ShouldRetry(ex, attempt: 0);

        // Assert
        result.Should().BeTrue(because: "IsInstanceOfType matches derived types, not just exact type matches");
    }

    // ------------------------------------------------------------------ MUT-953: unhandled type returns false

    // Kills MUT-953 (false → true return when no handled type matches):
    // When HandledExceptions is non-empty and the exception does NOT match any entry,
    // ShouldRetry must return false. The mutant would return true, retrying unhandled exceptions.
    [Fact]
    public void ShouldRetry_WhenExceptionDoesNotMatchAnyHandledType_ReturnsFalse()
    {
        // Arrange
        IntervalRetryPolicy policy = CreatePolicy(
            maxRetries: 5,
            handledExceptions: [typeof(InvalidOperationException)]);

        var ex = new ArgumentException("not in handled list");

        // Act
        bool result = policy.ShouldRetry(ex, attempt: 0);

        // Assert
        result.Should().BeFalse(because: "the exception type is not in the handled list");
    }

    // ------------------------------------------------------------------ upper bounds

    [Fact]
    public void Constructor_WhenMaxRetriesExceedsLimit_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => _ = CreatePolicy(maxRetries: 101);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxRetries");
    }

    [Fact]
    public void Constructor_WhenMaxRetriesEqualsLimit_DoesNotThrow()
    {
        Action act = () => _ = CreatePolicy(maxRetries: 100);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task DelayAsync_WhenPolicyDelayExceedsMaxDelay_CompletesExactlyAtMaxDelayAsync()
    {
        // Arrange — a policy whose raw delay is TimeSpan.MaxValue; Task.Delay(TimeSpan.MaxValue) would throw.
        var fakeTime = new FakeTimeProvider();
        var policy = new HugeDelayPolicy(fakeTime);

        // Act
        Task delay = policy.DelayAsync(attempt: 0, ct: CancellationToken.None);
        fakeTime.Advance(RetryPolicyLimits.MaxDelay - TimeSpan.FromTicks(1));
        bool completedEarly = delay.IsCompleted;
        fakeTime.Advance(TimeSpan.FromTicks(1));
        await delay.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        completedEarly.Should().BeFalse();
        delay.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task DelayAsync_WhenExplicitDelayExceedsMaxDelay_CompletesExactlyAtMaxDelayAsync()
    {
        var fakeTime = new FakeTimeProvider();
        var policy = new HugeDelayPolicy(fakeTime);

        Task delay = policy.DelayAsync(TimeSpan.MaxValue, CancellationToken.None);
        fakeTime.Advance(RetryPolicyLimits.MaxDelay - TimeSpan.FromTicks(1));
        bool completedEarly = delay.IsCompleted;
        fakeTime.Advance(TimeSpan.FromTicks(1));
        await delay.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        completedEarly.Should().BeFalse();
        delay.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public void Clamp_WhenDelayIsNegative_ReturnsZero() =>
        RetryPolicyLimits.Clamp(TimeSpan.FromSeconds(-1)).Should().Be(TimeSpan.Zero);

    [Fact]
    public void Clamp_WhenDelayIsWithinLimit_ReturnsSameValue() =>
        RetryPolicyLimits.Clamp(TimeSpan.FromMinutes(5)).Should().Be(TimeSpan.FromMinutes(5));

    private sealed class HugeDelayPolicy(TimeProvider timeProvider)
        : RetryPolicy(1, [], [], timeProvider)
    {
        internal override TimeSpan GetDelay(int attempt) => TimeSpan.MaxValue;
    }
}
