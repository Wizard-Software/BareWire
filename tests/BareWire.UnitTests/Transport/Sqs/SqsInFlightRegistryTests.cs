using AwesomeAssertions;
using BareWire.Transport.AWS.SQS.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.Sqs;

public sealed class SqsInFlightRegistryTests
{
    [Fact]
    public void Register_AndEvict_ReturnsRegisteredValues()
    {
        var registry = new SqsInFlightRegistry(maxSize: 100);

        bool registered = registry.TryRegister(
            deliveryTag: 1UL,
            receiptHandle: "handle-abc",
            queueUrl: "https://sqs.eu-central-1.amazonaws.com/123/my-queue");

        registered.Should().BeTrue();

        (string ReceiptHandle, string QueueUrl)? evicted = registry.TryEvict(1UL);

        evicted.Should().NotBeNull();
        evicted!.Value.ReceiptHandle.Should().Be("handle-abc");
        evicted.Value.QueueUrl.Should().Be("https://sqs.eu-central-1.amazonaws.com/123/my-queue");
    }

    [Fact]
    public void TryEvict_SecondCall_ReturnsNull()
    {
        // Evict-once: second evict for same tag must return null (no double-settle).
        var registry = new SqsInFlightRegistry(maxSize: 100);
        registry.TryRegister(1UL, "handle", "https://sqs/queue");

        (string, string)? first = registry.TryEvict(1UL);
        (string, string)? second = registry.TryEvict(1UL);

        first.Should().NotBeNull("first evict should succeed");
        second.Should().BeNull("second evict must return null (evict-once)");
    }

    [Fact]
    public void TryEvict_UnknownDeliveryTag_ReturnsNull()
    {
        var registry = new SqsInFlightRegistry(maxSize: 100);

        (string, string)? result = registry.TryEvict(42UL);

        result.Should().BeNull("evicting a non-existent tag should return null");
    }

    [Fact]
    public void Count_ReflectsRegistrations()
    {
        var registry = new SqsInFlightRegistry(maxSize: 100);

        registry.Count.Should().Be(0);

        registry.TryRegister(1UL, "h1", "url1");
        registry.Count.Should().Be(1);

        registry.TryRegister(2UL, "h2", "url2");
        registry.Count.Should().Be(2);

        registry.TryEvict(1UL);
        registry.Count.Should().Be(1);
    }

    [Fact]
    public void TryRegister_WhenAtMaxSize_ReturnsFalse()
    {
        var registry = new SqsInFlightRegistry(maxSize: 2);

        bool first = registry.TryRegister(1UL, "h1", "url1");
        bool second = registry.TryRegister(2UL, "h2", "url2");
        bool third = registry.TryRegister(3UL, "h3", "url3");

        first.Should().BeTrue();
        second.Should().BeTrue();
        third.Should().BeFalse("registry is at maxSize=2, third registration must be rejected");
    }

    [Fact]
    public void TryRegister_AfterEviction_AllowsNewRegistration()
    {
        var registry = new SqsInFlightRegistry(maxSize: 1);

        bool first = registry.TryRegister(1UL, "h1", "url1");
        registry.TryEvict(1UL); // free the slot

        bool second = registry.TryRegister(2UL, "h2", "url2");

        first.Should().BeTrue();
        second.Should().BeTrue("after eviction the slot should be free");
    }

    [Fact]
    public void Constructor_MaxSizeZero_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => _ = new SqsInFlightRegistry(maxSize: 0);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxSize");
    }
}
