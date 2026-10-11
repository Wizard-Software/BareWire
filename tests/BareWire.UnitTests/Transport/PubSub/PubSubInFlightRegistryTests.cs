using AwesomeAssertions;
using BareWire.Transport.Google.PubSub.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.PubSub;

public sealed class PubSubInFlightRegistryTests
{
    // ── Register / Evict ──────────────────────────────────────────────────────

    [Fact]
    public void TryRegister_NewEntry_ReturnsTrue()
    {
        var registry = new PubSubInFlightRegistry(maxSize: 10);

        bool result = registry.TryRegister(deliveryTag: 1, ackId: "ack-1", subscriptionName: "sub-a");

        result.Should().BeTrue();
    }

    [Fact]
    public void TryEvict_RegisteredEntry_ReturnsEntry()
    {
        var registry = new PubSubInFlightRegistry(maxSize: 10);
        registry.TryRegister(deliveryTag: 1, ackId: "ack-1", subscriptionName: "sub-a");

        (string AckId, string SubscriptionName)? entry = registry.TryEvict(1);

        entry.Should().NotBeNull();
        entry!.Value.AckId.Should().Be("ack-1");
        entry.Value.SubscriptionName.Should().Be("sub-a");
    }

    // ── Evict-once semantics ──────────────────────────────────────────────────

    [Fact]
    public void TryEvict_AlreadyEvictedEntry_ReturnsNull()
    {
        var registry = new PubSubInFlightRegistry(maxSize: 10);
        registry.TryRegister(deliveryTag: 1, ackId: "ack-1", subscriptionName: "sub-a");

        // First eviction succeeds.
        registry.TryEvict(1);

        // Second eviction for the same tag must return null (evict-once).
        (string AckId, string SubscriptionName)? second = registry.TryEvict(1);

        second.Should().BeNull("evict-once: second eviction for the same tag must return null");
    }

    // ── Miss ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TryEvict_NonExistentEntry_ReturnsNull()
    {
        var registry = new PubSubInFlightRegistry(maxSize: 10);

        (string AckId, string SubscriptionName)? result = registry.TryEvict(deliveryTag: 999);

        result.Should().BeNull("evicting a non-existent tag must return null");
    }

    // ── Capacity limit ────────────────────────────────────────────────────────

    [Fact]
    public void TryRegister_AtCapacity_ReturnsFalse()
    {
        const int maxSize = 3;
        var registry = new PubSubInFlightRegistry(maxSize: maxSize);

        // Fill to capacity.
        for (ulong i = 1; i <= maxSize; i++)
        {
            registry.TryRegister(deliveryTag: i, ackId: $"ack-{i}", subscriptionName: "sub-a")
                .Should().BeTrue($"registration {i} should succeed while under capacity");
        }

        // Next registration must fail.
        bool atCapacity = registry.TryRegister(
            deliveryTag: (ulong)maxSize + 1, ackId: "ack-extra", subscriptionName: "sub-a");

        atCapacity.Should().BeFalse("registry must reject registrations when at capacity (PERF-3)");
    }

    [Fact]
    public void Count_ReflectsCurrentEntries()
    {
        var registry = new PubSubInFlightRegistry(maxSize: 10);

        registry.Count.Should().Be(0);

        registry.TryRegister(1, "ack-1", "sub");
        registry.Count.Should().Be(1);

        registry.TryRegister(2, "ack-2", "sub");
        registry.Count.Should().Be(2);

        registry.TryEvict(1);
        registry.Count.Should().Be(1);
    }

    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_MaxSizeZero_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => _ = new PubSubInFlightRegistry(maxSize: 0);

        act.Should().ThrowExactly<ArgumentOutOfRangeException>()
            .WithParameterName("maxSize");
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
        var registry = new PubSubInFlightRegistry(limit);
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
        var registry = new PubSubInFlightRegistry(maxSize: 10);

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
        var registry = new PubSubInFlightRegistry(maxSize: 5);
        registry.TryRegister(1UL, "a1", "sub-a");
        registry.TryRegister(2UL, "a2", "sub-a");

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
        var registry = new PubSubInFlightRegistry(maxSize);
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
        var registry = new PubSubInFlightRegistry(maxSize: 4);
        registry.TryReserve(4).Should().Be(4);

        registry.ReleaseReservations(3);

        registry.Count.Should().Be(1);
        registry.TryReserve(10).Should().Be(3);
    }

    [Fact]
    public void RegisterReserved_ConvertsReservationIntoEntryWithoutChangingCount()
    {
        var registry = new PubSubInFlightRegistry(maxSize: 2);
        registry.TryReserve(2).Should().Be(2);

        bool registered = registry.RegisterReserved(1UL, "a1", "sub-a");

        registered.Should().BeTrue();
        registry.Count.Should().Be(2);
        registry.TryEvict(1UL).Should().NotBeNull();
        registry.Count.Should().Be(1);
    }

    [Fact]
    public void RegisterReserved_DuplicateTag_ReturnsFalseAndKeepsReservation()
    {
        var registry = new PubSubInFlightRegistry(maxSize: 2);
        registry.TryReserve(2);
        registry.RegisterReserved(1UL, "a1", "sub-a").Should().BeTrue();

        bool duplicate = registry.RegisterReserved(1UL, "a1b", "sub-a");

        duplicate.Should().BeFalse();
        registry.Count.Should().Be(2, "the failed registration leaves its reservation to the caller");
    }

    [Fact]
    public void TryEvict_RegisteredEntry_DecrementsCount()
    {
        var registry = new PubSubInFlightRegistry(maxSize: 3);
        registry.TryRegister(1UL, "a1", "sub-a");
        registry.TryRegister(2UL, "a2", "sub-a");

        registry.TryEvict(1UL);
        registry.TryEvict(1UL); // second evict must not decrement again
        registry.TryEvict(99UL); // unknown tag must not decrement

        registry.Count.Should().Be(1);
    }

    [Fact]
    public void TryRegister_DuplicateTag_ReturnsFalseAndDoesNotLeakSlot()
    {
        var registry = new PubSubInFlightRegistry(maxSize: 3);
        registry.TryRegister(1UL, "a1", "sub-a").Should().BeTrue();

        registry.TryRegister(1UL, "a1b", "sub-a").Should().BeFalse();

        registry.Count.Should().Be(1);
    }
}
