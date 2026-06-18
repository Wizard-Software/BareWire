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
}
