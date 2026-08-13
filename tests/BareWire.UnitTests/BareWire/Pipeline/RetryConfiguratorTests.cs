using AwesomeAssertions;
using BareWire.Pipeline.Retry;
using Microsoft.Extensions.Time.Testing;

namespace BareWire.UnitTests.Core.Pipeline;

public sealed class RetryConfiguratorTests
{
    // MUT-922, MUT-923: null guard on _policyFactory — Build() must throw when no strategy configured
    [Fact]
    public void Build_WhenNoStrategyConfigured_ThrowsInvalidOperationException()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        Action act = () => sut.Build();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Interval*Incremental*Exponential*");
    }

    // MUT-914: Interval() body removal — must produce IntervalRetryPolicy
    // Build() lives on the concrete RetryConfigurator (not on the public zero-dep IRetryConfigurator),
    // so it is invoked on the concrete `sut` after configuring it fluently.
    [Fact]
    public void Interval_WhenCalled_BuildProducesIntervalRetryPolicy()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        sut.Interval(3, TimeSpan.FromSeconds(1));
        RetryPolicy policy = sut.Build();

        // Assert
        policy.Should().BeOfType<IntervalRetryPolicy>();
    }

    // MUT-914: Interval() wires correct MaxRetries
    [Fact]
    public void Interval_WhenCalled_BuiltPolicyHasCorrectMaxRetries()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        sut.Interval(5, TimeSpan.FromSeconds(2));
        RetryPolicy policy = sut.Build();

        // Assert
        policy.MaxRetries.Should().Be(5);
    }

    // MUT-914: Interval() wires correct delay
    [Fact]
    public void Interval_WhenCalled_BuiltPolicyReturnsConfiguredInterval()
    {
        // Arrange
        var interval = TimeSpan.FromSeconds(3);
        var sut = new RetryConfigurator();

        // Act
        sut.Interval(2, interval);
        RetryPolicy policy = sut.Build();

        // Assert
        policy.GetDelay(0).Should().Be(interval);
    }

    // MUT-915: Incremental() body removal — must produce IncrementalRetryPolicy
    [Fact]
    public void Incremental_WhenCalled_BuildProducesIncrementalRetryPolicy()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        sut.Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        RetryPolicy policy = sut.Build();

        // Assert
        policy.Should().BeOfType<IncrementalRetryPolicy>();
    }

    // MUT-915: Incremental() wires correct MaxRetries
    [Fact]
    public void Incremental_WhenCalled_BuiltPolicyHasCorrectMaxRetries()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        sut.Incremental(4, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        RetryPolicy policy = sut.Build();

        // Assert
        policy.MaxRetries.Should().Be(4);
    }

    // MUT-915: Incremental() wires correct delay progression
    [Fact]
    public void Incremental_WhenCalled_BuiltPolicyReturnsIncrementingDelays()
    {
        // Arrange — initial=1s, increment=2s => attempt 0 = 1s, attempt 1 = 3s
        var sut = new RetryConfigurator();

        // Act
        sut.Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        RetryPolicy policy = sut.Build();

        // Assert
        policy.GetDelay(0).Should().Be(TimeSpan.FromSeconds(1));
        policy.GetDelay(1).Should().Be(TimeSpan.FromSeconds(3));
    }

    // MUT-916: Exponential() body removal — must produce ExponentialRetryPolicy
    [Fact]
    public void Exponential_WhenCalled_BuildProducesExponentialRetryPolicy()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        sut.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        RetryPolicy policy = sut.Build();

        // Assert
        policy.Should().BeOfType<ExponentialRetryPolicy>();
    }

    // MUT-916: Exponential() wires correct MaxRetries
    [Fact]
    public void Exponential_WhenCalled_BuiltPolicyHasCorrectMaxRetries()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        sut.Exponential(6, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        RetryPolicy policy = sut.Build();

        // Assert
        policy.MaxRetries.Should().Be(6);
    }

    // MUT-918: Handle<T>() Add() removal — handled exceptions list must contain the registered type
    [Fact]
    public void Handle_WhenCalled_BuiltPolicyHandledExceptionsContainsRegisteredType()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        sut.Handle<InvalidOperationException>()
            .Interval(3, TimeSpan.Zero);
        RetryPolicy policy = sut.Build();

        // Assert
        policy.HandledExceptions.Should().Contain(typeof(InvalidOperationException));
    }

    // MUT-918: multiple Handle<T>() calls — all types must appear
    [Fact]
    public void Handle_WhenCalledMultipleTimes_BuiltPolicyHandledExceptionsContainsAllRegisteredTypes()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        sut.Handle<InvalidOperationException>()
            .Handle<TimeoutException>()
            .Interval(3, TimeSpan.Zero);
        RetryPolicy policy = sut.Build();

        // Assert
        policy.HandledExceptions.Should().Contain(typeof(InvalidOperationException));
        policy.HandledExceptions.Should().Contain(typeof(TimeoutException));
    }

    // MUT-920: Ignore<T>() Add() removal — ignored exceptions list must contain the registered type
    [Fact]
    public void Ignore_WhenCalled_BuiltPolicyIgnoredExceptionsContainsRegisteredType()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        sut.Ignore<ArgumentException>()
            .Interval(3, TimeSpan.Zero);
        RetryPolicy policy = sut.Build();

        // Assert
        policy.IgnoredExceptions.Should().Contain(typeof(ArgumentException));
    }

    // MUT-920: multiple Ignore<T>() calls — all types must appear
    [Fact]
    public void Ignore_WhenCalledMultipleTimes_BuiltPolicyIgnoredExceptionsContainsAllRegisteredTypes()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        sut.Ignore<ArgumentException>()
            .Ignore<OperationCanceledException>()
            .Interval(3, TimeSpan.Zero);
        RetryPolicy policy = sut.Build();

        // Assert
        policy.IgnoredExceptions.Should().Contain(typeof(ArgumentException));
        policy.IgnoredExceptions.Should().Contain(typeof(OperationCanceledException));
    }

    // MUT-914: Interval() block removal — method must return the same configurator instance (fluent)
    [Fact]
    public void Interval_WhenCalled_ReturnsSameConfiguratorInstance()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        var result = sut.Interval(3, TimeSpan.FromSeconds(1));

        // Assert — block removal returns null/default; this fails cleanly without chaining .Build()
        result.Should().BeSameAs(sut);
    }

    // MUT-915: Incremental() block removal — method must return the same configurator instance (fluent)
    [Fact]
    public void Incremental_WhenCalled_ReturnsSameConfiguratorInstance()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        var result = sut.Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        // Assert — block removal returns null/default; this fails cleanly without chaining .Build()
        result.Should().BeSameAs(sut);
    }

    // MUT-916: Exponential() block removal — method must return the same configurator instance (fluent)
    [Fact]
    public void Exponential_WhenCalled_ReturnsSameConfiguratorInstance()
    {
        // Arrange
        var sut = new RetryConfigurator();

        // Act
        var result = sut.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        // Assert — block removal returns null/default; this fails cleanly without chaining .Build()
        result.Should().BeSameAs(sut);
    }

    // MUT-913: Constructor body removal — TimeProvider passed in must reach the built policy
    // Verified indirectly: FakeTimeProvider.GetUtcNow() is called via Task.Delay inside DelayAsync;
    // we confirm the constructor wired the provider by checking that the policy uses it for scheduling.
    // The observable signal is that Task.Delay is driven by the fake clock, not the real clock.
    [Fact]
    public async Task Constructor_WhenTimeProviderGiven_BuiltPolicyUsesProvidedTimeProvider()
    {
        // Arrange
        var fakeTime = new FakeTimeProvider();
        var sut = new RetryConfigurator(fakeTime);
        sut.Interval(3, TimeSpan.FromSeconds(10));
        RetryPolicy policy = sut.Build();

        using var cts = new CancellationTokenSource();

        // Act — start a delay driven by the fake provider; it must not complete unless we advance the clock
        Task delayTask = policy.DelayAsync(0, cts.Token);

        // The task must not be complete yet (fake clock has not advanced)
        delayTask.IsCompleted.Should().BeFalse();

        // Advance the fake clock past the interval to unblock the delay
        fakeTime.Advance(TimeSpan.FromSeconds(10));

        // Give the scheduler a chance to complete the task
        await Task.Delay(50, CancellationToken.None);

        // Assert — task completes after advancing the fake clock (i.e., the provided TimeProvider is used)
        delayTask.IsCompleted.Should().BeTrue();
    }
}
