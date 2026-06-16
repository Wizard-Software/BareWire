using AwesomeAssertions;
using BareWire.Transport.Kafka.Internal;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class RetryDlqTopicNamePolicyTests
{
    private static KafkaRetryDlqOptions DefaultOptions() => new();

    [Fact]
    public void ResolveRetryTopic_DefaultSuffix_AppendsDotRetry()
    {
        // Act
        string retry = RetryDlqTopicNamePolicy.ResolveRetryTopic("orders", DefaultOptions());

        // Assert
        retry.Should().Be("orders.retry");
    }

    [Fact]
    public void ResolveDlqTopic_DefaultSuffix_AppendsDotDlq()
    {
        // Act
        string dlq = RetryDlqTopicNamePolicy.ResolveDlqTopic("orders", DefaultOptions());

        // Assert
        dlq.Should().Be("orders.DLQ");
    }

    [Fact]
    public void ResolveRetryTopic_CustomSuffix_UsesConfiguredSuffix()
    {
        // Arrange
        var options = new KafkaRetryDlqOptions { RetryTopicSuffix = "-retry-v2" };

        // Act
        string retry = RetryDlqTopicNamePolicy.ResolveRetryTopic("payments", options);

        // Assert
        retry.Should().Be("payments-retry-v2");
    }

    [Fact]
    public void ResolveRetryTopic_AlreadyHasSuffix_IsIdempotent()
    {
        // Arrange — a message already on the retry-topic must not become orders.retry.retry
        var options = DefaultOptions();

        // Act
        string retry = RetryDlqTopicNamePolicy.ResolveRetryTopic("orders.retry", options);

        // Assert
        retry.Should().Be("orders.retry");
    }

    [Fact]
    public void ResolveDlqTopic_AlreadyHasSuffix_IsIdempotent()
    {
        // Arrange
        var options = DefaultOptions();

        // Act
        string dlq = RetryDlqTopicNamePolicy.ResolveDlqTopic("orders.DLQ", options);

        // Assert
        dlq.Should().Be("orders.DLQ");
    }

    [Fact]
    public void ResolveRetryTopic_NullSourceTopic_ThrowsArgumentException()
    {
        // Act
        Action act = () => RetryDlqTopicNamePolicy.ResolveRetryTopic(null!, DefaultOptions());

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ResolveDlqTopic_EmptySourceTopic_ThrowsArgumentException()
    {
        // Act
        Action act = () => RetryDlqTopicNamePolicy.ResolveDlqTopic("", DefaultOptions());

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
