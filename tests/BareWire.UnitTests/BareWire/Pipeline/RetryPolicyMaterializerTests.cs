using AwesomeAssertions;
using BareWire.Pipeline.Retry;

namespace BareWire.UnitTests.Core.Pipeline;

public sealed class RetryPolicyMaterializerTests
{
    [Fact]
    public void Materialize_WhenConfigureIsNull_ReturnsNull()
    {
        // Act
        RetryPolicy? policy = RetryPolicyMaterializer.Materialize(null);

        // Assert
        policy.Should().BeNull();
    }

    [Fact]
    public void Materialize_WithInterval_ProducesIntervalRetryPolicy()
    {
        // Act
        RetryPolicy? policy = RetryPolicyMaterializer.Materialize(
            r => r.Interval(3, TimeSpan.FromSeconds(1)));

        // Assert
        policy.Should().BeOfType<IntervalRetryPolicy>();
    }

    [Fact]
    public void Materialize_WithIncremental_ProducesIncrementalRetryPolicy()
    {
        // Act
        RetryPolicy? policy = RetryPolicyMaterializer.Materialize(
            r => r.Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

        // Assert
        policy.Should().BeOfType<IncrementalRetryPolicy>();
    }

    [Fact]
    public void Materialize_WithExponential_ProducesExponentialRetryPolicy()
    {
        // Act
        RetryPolicy? policy = RetryPolicyMaterializer.Materialize(
            r => r.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)));

        // Assert
        policy.Should().BeOfType<ExponentialRetryPolicy>();
    }

    [Fact]
    public void Materialize_WithHandle_BuiltPolicyHandlesOnlyRegisteredException()
    {
        // Act
        RetryPolicy? policy = RetryPolicyMaterializer.Materialize(
            r => r.Interval(3, TimeSpan.FromMilliseconds(1)).Handle<InvalidOperationException>());

        // Assert
        policy!.ShouldRetry(new InvalidOperationException(), attempt: 0).Should().BeTrue();
        policy.ShouldRetry(new FormatException(), attempt: 0).Should().BeFalse();
    }

    [Fact]
    public void Materialize_WithIgnore_BuiltPolicyIgnoresRegisteredException()
    {
        // Act
        RetryPolicy? policy = RetryPolicyMaterializer.Materialize(
            r => r.Interval(3, TimeSpan.FromMilliseconds(1)).Ignore<FormatException>());

        // Assert
        policy!.ShouldRetry(new FormatException(), attempt: 0).Should().BeFalse();
        policy.ShouldRetry(new InvalidOperationException(), attempt: 0).Should().BeTrue();
    }

    [Fact]
    public void Materialize_WhenDelegateSelectsNoStrategy_ThrowsInvalidOperationException()
    {
        // Act
        Action act = () => RetryPolicyMaterializer.Materialize(r => r.Handle<Exception>());

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }
}
