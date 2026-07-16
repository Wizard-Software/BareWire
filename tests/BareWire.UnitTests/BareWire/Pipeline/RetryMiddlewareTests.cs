using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Pipeline;
using BareWire.Pipeline;
using BareWire.Pipeline.Retry;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Pipeline;

public sealed class RetryMiddlewareTests
{
    private static MessageContext CreateContext(CancellationToken cancellationToken = default)
    {
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        return new MessageContext(
            messageId: Guid.NewGuid(),
            headers: new Dictionary<string, string>(),
            rawBody: ReadOnlySequence<byte>.Empty,
            serviceProvider: serviceProvider,
            cancellationToken: cancellationToken);
    }

    private static IntervalRetryPolicy CreateIntervalPolicy(
        int retryCount = 3,
        TimeSpan? interval = null,
        IReadOnlyList<Type>? handled = null,
        IReadOnlyList<Type>? ignored = null)
    {
        return new IntervalRetryPolicy(
            retryCount,
            interval ?? TimeSpan.Zero,
            handled ?? [],
            ignored ?? []);
    }

    [Fact]
    public async Task InvokeAsync_FirstAttemptSucceeds_NoRetry()
    {
        // Arrange
        int callCount = 0;
        var policy = CreateIntervalPolicy();
        var sut = new RetryMiddleware(
            policy,
            NullLogger<RetryMiddleware>.Instance,
            Substitute.For<IBareWireInstrumentation>(),
            "TestMessage");
        var context = CreateContext();

        // Act
        await sut.InvokeAsync(context, _ =>
        {
            callCount++;
            return Task.CompletedTask;
        });

        // Assert
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task InvokeAsync_TransientFailure_RetriesUpToMaxCount()
    {
        // Arrange
        int callCount = 0;
        var policy = CreateIntervalPolicy(retryCount: 3);
        var sut = new RetryMiddleware(
            policy,
            NullLogger<RetryMiddleware>.Instance,
            Substitute.For<IBareWireInstrumentation>(),
            "TestMessage");
        var context = CreateContext();

        // Act
        await sut.InvokeAsync(context, _ =>
        {
            callCount++;
            if (callCount < 3)
                throw new InvalidOperationException("transient");
            return Task.CompletedTask;
        });

        // Assert — initial call + 2 retries = 3 total
        callCount.Should().Be(3);
    }

    [Fact]
    public void InvokeAsync_IntervalStrategy_DelaysBetweenRetries()
    {
        // Verify IntervalRetryPolicy returns the same delay regardless of attempt.
        var interval = TimeSpan.FromSeconds(5);
        var policy = new IntervalRetryPolicy(3, interval, [], []);

        policy.GetDelay(0).Should().Be(interval);
        policy.GetDelay(1).Should().Be(interval);
        policy.GetDelay(2).Should().Be(interval);
    }

    [Fact]
    public void InvokeAsync_IncrementalStrategy_IncreasingDelays()
    {
        // Verify IncrementalRetryPolicy returns increasing delays: initial + increment * attempt.
        var initial = TimeSpan.FromSeconds(1);
        var increment = TimeSpan.FromSeconds(2);
        var policy = new IncrementalRetryPolicy(3, initial, increment, [], []);

        policy.GetDelay(0).Should().Be(TimeSpan.FromSeconds(1));  // 1 + 2*0
        policy.GetDelay(1).Should().Be(TimeSpan.FromSeconds(3));  // 1 + 2*1
        policy.GetDelay(2).Should().Be(TimeSpan.FromSeconds(5));  // 1 + 2*2
    }

    [Fact]
    public void InvokeAsync_ExponentialStrategy_ExponentialBackoff()
    {
        // Verify ExponentialRetryPolicy returns exponentially growing delays, capped at max.
        var min = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(60);
        var policy = new ExponentialRetryPolicy(5, min, max, [], []);

        // Delays are min * 2^attempt with ±10% jitter, capped at max.
        // We verify they grow and stay within bounds.
        TimeSpan delay0 = policy.GetDelay(0);
        TimeSpan delay1 = policy.GetDelay(1);
        TimeSpan delay2 = policy.GetDelay(2);

        delay0.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));  // 1s - 10%
        delay0.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(1100));    // 1s + 10%
        delay1.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1800)); // 2s - 10%
        delay2.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(3600)); // 4s - 10%
        delay2.Should().BeLessThanOrEqualTo(max);
    }

    [Fact]
    public async Task InvokeAsync_RetryWithDelay_ActuallyDelaysBetweenAttempts()
    {
        // Arrange — use a short real delay (50ms) and verify total time reflects retries.
        var interval = TimeSpan.FromMilliseconds(50);
        var policy = new IntervalRetryPolicy(2, interval, [], []);
        var sut = new RetryMiddleware(
            policy,
            NullLogger<RetryMiddleware>.Instance,
            Substitute.For<IBareWireInstrumentation>(),
            "TestMessage");
        var context = CreateContext();
        int callCount = 0;

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.InvokeAsync(context, _ =>
        {
            callCount++;
            if (callCount <= 2)
                throw new InvalidOperationException("transient");
            return Task.CompletedTask;
        });
        sw.Stop();

        // Assert — 2 retries × 50ms delay = ~100ms minimum
        callCount.Should().Be(3);
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(80));
    }

    [Fact]
    public async Task InvokeAsync_HandleFilter_OnlyRetriesMatchingExceptions()
    {
        // Arrange
        var policy = CreateIntervalPolicy(
            retryCount: 3,
            handled: [typeof(InvalidOperationException)]);
        var sut = new RetryMiddleware(
            policy,
            NullLogger<RetryMiddleware>.Instance,
            Substitute.For<IBareWireInstrumentation>(),
            "TestMessage");
        var context = CreateContext();

        // Act — ArgumentException is NOT in Handle list, should not retry
        Func<Task> act = () => sut.InvokeAsync(context, _ =>
            throw new ArgumentException("not handled"));

        // Assert — original exception propagated without retry
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task InvokeAsync_IgnoreFilter_NeverRetriesIgnoredExceptions()
    {
        // Arrange
        var policy = CreateIntervalPolicy(
            retryCount: 3,
            ignored: [typeof(ArgumentException)]);
        var sut = new RetryMiddleware(
            policy,
            NullLogger<RetryMiddleware>.Instance,
            Substitute.For<IBareWireInstrumentation>(),
            "TestMessage");
        var context = CreateContext();
        int callCount = 0;

        // Act — ArgumentException is in Ignore list, should not retry
        Func<Task> act = () => sut.InvokeAsync(context, _ =>
        {
            callCount++;
            throw new ArgumentException("ignored");
        });

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task InvokeAsync_MaxRetriesExceeded_ThrowsOriginalException()
    {
        // Arrange
        var policy = CreateIntervalPolicy(retryCount: 2);
        var sut = new RetryMiddleware(
            policy,
            NullLogger<RetryMiddleware>.Instance,
            Substitute.For<IBareWireInstrumentation>(),
            "TestMessage");
        var context = CreateContext();
        int callCount = 0;

        // Act + Assert
        Func<Task> act = () => sut.InvokeAsync(context, _ =>
        {
            callCount++;
            throw new InvalidOperationException("persistent failure");
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("persistent failure");
        callCount.Should().Be(3); // 1 initial + 2 retries
    }

    [Fact]
    public async Task InvokeAsync_CancellationRequested_ThrowsImmediately()
    {
        // Arrange — use real short delay so cancellation has something to cancel
        using var cts = new CancellationTokenSource();
        var policy = CreateIntervalPolicy(retryCount: 5, interval: TimeSpan.FromSeconds(60));
        var sut = new RetryMiddleware(
            policy,
            NullLogger<RetryMiddleware>.Instance,
            Substitute.For<IBareWireInstrumentation>(),
            "TestMessage");
        var context = CreateContext(cts.Token);
        int callCount = 0;

        // Act — start the retry, it will enter a 60s delay after first failure
        Task retryTask = sut.InvokeAsync(context, _ =>
        {
            callCount++;
            throw new InvalidOperationException("will retry");
        });

        // Give it a moment to enter the delay
        await Task.Delay(50);
        callCount.Should().Be(1);

        // Cancel during delay
        cts.Cancel();

        // Assert — OperationCanceledException propagated
        Func<Task> act = () => retryTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
