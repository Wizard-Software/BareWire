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

    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(1, 100, 100)]
    [InlineData(2, 100, 50)]
    [InlineData(3, 100, 33)]
    [InlineData(4, 10, 2)]
    [InlineData(200, 100, 1)]
    public void GetFairShare_WithActiveConsumers_CapsRequestToLimitDividedByConsumers(
        int consumers, int limit, int expectedCap)
    {
        var registry = new SqsInFlightRegistry(limit);
        for (int i = 0; i < consumers; i++)
        {
            registry.EnterConsumer();
        }

        registry.GetFairShare(limit).Should().Be(expectedCap);
        registry.GetFairShare(1).Should().Be(1, "the cap never raises a smaller request");
        registry.GetFairShare(0).Should().Be(0);
    }

    [Fact]
    public void ExitConsumer_WithoutEnter_NeverDropsBelowZero()
    {
        var registry = new SqsInFlightRegistry(maxSize: 10);

        registry.ExitConsumer();
        registry.EnterConsumer();

        registry.ActiveConsumers.Should().Be(1);
        registry.ExitConsumer();
        registry.ExitConsumer();
        registry.ActiveConsumers.Should().Be(0);
    }

    [Fact]
    public void TryReserve_WhenPartiallyFull_GrantsOnlyFreeSlots()
    {
        var registry = new SqsInFlightRegistry(maxSize: 5);
        registry.TryRegister(1UL, "h1", "url1");
        registry.TryRegister(2UL, "h2", "url2");

        int granted = registry.TryReserve(10);

        granted.Should().Be(3);
        registry.Count.Should().Be(5, "reserved slots count as occupied");
        registry.TryReserve(1).Should().Be(0, "no free slot is left");
    }

    [Fact]
    public void TryReserve_ConcurrentCallers_NeverExceedMaxSize()
    {
        const int maxSize = 50;
        const int callers = 8;
        var registry = new SqsInFlightRegistry(maxSize);
        int totalGranted = 0;

        Parallel.For(0, callers, new ParallelOptions { MaxDegreeOfParallelism = callers }, _ =>
        {
            for (int i = 0; i < 100; i++)
            {
                Interlocked.Add(ref totalGranted, registry.TryReserve(3));
            }
        });

        totalGranted.Should().Be(maxSize);
        registry.Count.Should().Be(maxSize);
    }

    [Fact]
    public void ReleaseReservations_ReturnsSlots()
    {
        var registry = new SqsInFlightRegistry(maxSize: 4);
        registry.TryReserve(4).Should().Be(4);

        registry.ReleaseReservations(3);

        registry.Count.Should().Be(1);
        registry.TryReserve(10).Should().Be(3);
    }

    [Fact]
    public void RegisterReserved_ConvertsReservationIntoEntryWithoutChangingCount()
    {
        var registry = new SqsInFlightRegistry(maxSize: 2);
        registry.TryReserve(2).Should().Be(2);

        bool registered = registry.RegisterReserved(1UL, "h1", "url1");

        registered.Should().BeTrue();
        registry.Count.Should().Be(2);
        registry.TryEvict(1UL).Should().NotBeNull();
        registry.Count.Should().Be(1);
    }

    [Fact]
    public void RegisterReserved_DuplicateTag_ReturnsFalseAndKeepsReservation()
    {
        var registry = new SqsInFlightRegistry(maxSize: 2);
        registry.TryReserve(2);
        registry.RegisterReserved(1UL, "h1", "url1").Should().BeTrue();

        bool duplicate = registry.RegisterReserved(1UL, "h1b", "url1");

        duplicate.Should().BeFalse();
        registry.Count.Should().Be(2, "the failed registration leaves its reservation to the caller");
    }

    [Fact]
    public void TryEvict_RegisteredEntry_DecrementsCount()
    {
        var registry = new SqsInFlightRegistry(maxSize: 3);
        registry.TryRegister(1UL, "h1", "url1");
        registry.TryRegister(2UL, "h2", "url2");

        registry.TryEvict(1UL);
        registry.TryEvict(1UL); // second evict must not decrement again
        registry.TryEvict(99UL); // unknown tag must not decrement

        registry.Count.Should().Be(1);
    }

    [Fact]
    public void TryRegister_DuplicateTag_ReturnsFalseAndDoesNotLeakSlot()
    {
        var registry = new SqsInFlightRegistry(maxSize: 3);
        registry.TryRegister(1UL, "h1", "url1").Should().BeTrue();

        registry.TryRegister(1UL, "h1b", "url1").Should().BeFalse();

        registry.Count.Should().Be(1);
    }
}
