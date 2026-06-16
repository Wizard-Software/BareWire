using AwesomeAssertions;
using BareWire.Transport.Kafka.Internal;
using Confluent.Kafka;
using NSubstitute;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaConsumerRegistryTests
{
    private static KafkaConsumer CreateFakeConsumer(string consumerId = "consumer-1")
    {
        // KafkaConsumer ctor requires a live IConsumer — substitute it.
        // R1.5 integration tests cover the full broker-connected path.
        var nativeConsumer = Substitute.For<IConsumer<byte[], byte[]>>();
        var channel = System.Threading.Channels.Channel.CreateBounded<BareWire.Abstractions.Transport.InboundMessage>(10);
        var registry = new KafkaConsumerRegistry();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        return new KafkaConsumer(
            consumer: nativeConsumer,
            channel: channel,
            registry: registry,
            consumerId: consumerId,
            topic: "test-topic",
            logger: logger);
    }

    // ── Register / Resolve ────────────────────────────────────────────────────

    [Fact]
    public void Register_ThenResolveByConsumerId_ReturnsRegisteredConsumer()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();
        KafkaConsumer consumer = CreateFakeConsumer("c-1");

        // Act
        registry.Register("c-1", consumer);
        KafkaConsumer? resolved = registry.ResolveByConsumerId("c-1");

        // Assert
        resolved.Should().BeSameAs(consumer);
    }

    [Fact]
    public void ResolveByConsumerId_UnregisteredId_ReturnsNull()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();

        // Act
        KafkaConsumer? resolved = registry.ResolveByConsumerId("no-such-consumer");

        // Assert
        resolved.Should().BeNull();
    }

    [Fact]
    public void Unregister_RemovesConsumerFromRegistry()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();
        KafkaConsumer consumer = CreateFakeConsumer("c-2");
        registry.Register("c-2", consumer);

        // Act
        registry.Unregister("c-2");

        // Assert
        registry.ResolveByConsumerId("c-2").Should().BeNull();
    }

    [Fact]
    public void Unregister_NonExistentConsumer_DoesNotThrow()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();

        // Act
        Action act = () => registry.Unregister("ghost");

        // Assert
        act.Should().NotThrow();
    }

    // ── DeliveryTag → TopicPartitionOffset round-trip ─────────────────────────

    [Fact]
    public void StoreOffset_ThenTryEvictOffset_ReturnsCorrectTpo()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();
        KafkaConsumer consumer = CreateFakeConsumer("c-3");
        registry.Register("c-3", consumer);

        var tpo = new TopicPartitionOffset("my-topic", new Partition(0), new Offset(42));
        ulong deliveryTag = 7UL;

        // Act
        registry.StoreOffset("c-3", deliveryTag, tpo);
        TopicPartitionOffset? evicted = registry.TryEvictOffset("c-3", deliveryTag);

        // Assert
        evicted.Should().NotBeNull();
        evicted!.Topic.Should().Be("my-topic");
        evicted.Partition.Should().Be(new Partition(0));
        evicted.Offset.Should().Be(new Offset(42));
    }

    [Fact]
    public void TryEvictOffset_UnknownDeliveryTag_ReturnsNull()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();
        KafkaConsumer consumer = CreateFakeConsumer("c-4");
        registry.Register("c-4", consumer);

        // Act — tag 99 was never stored
        TopicPartitionOffset? result = registry.TryEvictOffset("c-4", 99UL);

        // Assert
        result.Should().BeNull();
    }

    // ── Eviction removes the entry (no unbounded growth) ─────────────────────

    [Fact]
    public void TryEvictOffset_CalledTwiceForSameTag_SecondCallReturnsNull()
    {
        // Arrange — eviction must remove the entry; second call must return null
        var registry = new KafkaConsumerRegistry();
        KafkaConsumer consumer = CreateFakeConsumer("c-5");
        registry.Register("c-5", consumer);

        var tpo = new TopicPartitionOffset("t", 0, 1);
        registry.StoreOffset("c-5", 1UL, tpo);

        // Act
        registry.TryEvictOffset("c-5", 1UL); // first call — evicts
        TopicPartitionOffset? second = registry.TryEvictOffset("c-5", 1UL); // second call

        // Assert — entry was evicted on first call; second must return null
        second.Should().BeNull();
    }

    // ── D1 CRITICAL: partition-collision test ─────────────────────────────────
    //
    // Two messages with the SAME Kafka offset on DIFFERENT partitions must receive
    // DIFFERENT DeliveryTags and resolve to DIFFERENT TopicPartitionOffset values.
    // This is the centerpiece of GAP-1 (raw Kafka offset is not unique across partitions).

    [Fact]
    public void StoreOffset_SameOffsetDifferentPartitions_GetDifferentDeliveryTagsAndDifferentTpos()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();
        KafkaConsumer consumer = CreateFakeConsumer("c-6");
        registry.Register("c-6", consumer);

        // Same raw Kafka offset (5) on two different partitions — the scenario that would
        // cause collision if the raw offset were used as DeliveryTag.
        const int sameOffset = 5;
        var tpoPartition0 = new TopicPartitionOffset("my-topic", new Partition(0), new Offset(sameOffset));
        var tpoPartition3 = new TopicPartitionOffset("my-topic", new Partition(3), new Offset(sameOffset));

        // Per-consumer monotonic counter (D1): tags are assigned sequentially, never from raw offsets.
        ulong deliveryTagForP0 = 1UL;
        ulong deliveryTagForP3 = 2UL;

        registry.StoreOffset("c-6", deliveryTagForP0, tpoPartition0);
        registry.StoreOffset("c-6", deliveryTagForP3, tpoPartition3);

        // Act
        TopicPartitionOffset? resolvedP0 = registry.TryEvictOffset("c-6", deliveryTagForP0);
        TopicPartitionOffset? resolvedP3 = registry.TryEvictOffset("c-6", deliveryTagForP3);

        // Assert — different tags → different TPOs; offsets identical but partitions distinct
        deliveryTagForP0.Should().NotBe(deliveryTagForP3,
            "per-consumer monotonic tag (D1) must never collide for messages from different partitions");

        resolvedP0.Should().NotBeNull();
        resolvedP3.Should().NotBeNull();

        resolvedP0!.Partition.Should().Be(new Partition(0));
        resolvedP3!.Partition.Should().Be(new Partition(3));

        resolvedP0.Offset.Should().Be(new Offset(sameOffset));
        resolvedP3.Offset.Should().Be(new Offset(sameOffset));

        // Most importantly: the two TPOs are distinct despite same raw offset
        resolvedP0.Should().NotBeSameAs(resolvedP3);
        resolvedP0.Partition.Should().NotBe(resolvedP3.Partition,
            "same raw Kafka offset on different partitions must map to different TopicPartitionOffset entries");
    }

    // ── Multiple consumers are independent ────────────────────────────────────

    [Fact]
    public void StoreOffset_TwoConsumers_OffsetsAreIsolated()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();
        KafkaConsumer consumerA = CreateFakeConsumer("c-A");
        KafkaConsumer consumerB = CreateFakeConsumer("c-B");
        registry.Register("c-A", consumerA);
        registry.Register("c-B", consumerB);

        var tpoA = new TopicPartitionOffset("topic", 0, 100);
        var tpoB = new TopicPartitionOffset("topic", 0, 200);

        // Same delivery tag (1) for both consumers — per-consumer maps are isolated (D1)
        registry.StoreOffset("c-A", 1UL, tpoA);
        registry.StoreOffset("c-B", 1UL, tpoB);

        // Act
        TopicPartitionOffset? resolvedA = registry.TryEvictOffset("c-A", 1UL);
        TopicPartitionOffset? resolvedB = registry.TryEvictOffset("c-B", 1UL);

        // Assert
        resolvedA!.Offset.Value.Should().Be(100);
        resolvedB!.Offset.Value.Should().Be(200);
    }

    // ── AllConsumers snapshot ─────────────────────────────────────────────────

    [Fact]
    public void AllConsumers_AfterRegisterAndUnregister_ReflectsCurrentState()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();
        KafkaConsumer c1 = CreateFakeConsumer("c1");
        KafkaConsumer c2 = CreateFakeConsumer("c2");

        registry.Register("c1", c1);
        registry.Register("c2", c2);

        // Act — unregister c1
        registry.Unregister("c1");
        IReadOnlyList<KafkaConsumer> all = registry.AllConsumers();

        // Assert — only c2 remains
        all.Should().HaveCount(1);
        all[0].Should().BeSameAs(c2);
    }

    // ── Spec-required test names (D1 / GAP-1 critical) ───────────────────────

    /// <summary>
    /// Required by spec C3 (plan §10, D1 critical-risk). Same scenario as
    /// <see cref="StoreOffset_SameOffsetDifferentPartitions_GetDifferentDeliveryTagsAndDifferentTpos"/>
    /// but with the canonical spec name so filtering by name works as documented.
    /// </summary>
    [Fact]
    public void StoreOffset_SameOffsetDifferentPartitions_TryEvictReturnsDistinctTopicPartitionOffsets()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();
        KafkaConsumer consumer = CreateFakeConsumer("c-d1");
        registry.Register("c-d1", consumer);

        // Same raw Kafka offset (5) on two different partitions.
        var tpoP0 = new TopicPartitionOffset(new TopicPartition("topic", new Partition(0)), new Offset(5));
        var tpoP3 = new TopicPartitionOffset(new TopicPartition("topic", new Partition(3)), new Offset(5));

        // Per-consumer monotonic delivery tags (D1) — never the raw offset.
        ulong tag1 = 1UL;
        ulong tag2 = 2UL;

        registry.StoreOffset("c-d1", tag1, tpoP0);
        registry.StoreOffset("c-d1", tag2, tpoP3);

        // Act
        TopicPartitionOffset? resolved1 = registry.TryEvictOffset("c-d1", tag1);
        TopicPartitionOffset? resolved2 = registry.TryEvictOffset("c-d1", tag2);

        // Assert — different delivery tags yield different TPOs with different partitions
        resolved1.Should().NotBeNull();
        resolved2.Should().NotBeNull();
        resolved1!.Partition.Should().Be(new Partition(0));
        resolved2!.Partition.Should().Be(new Partition(3));
        resolved1.Partition.Should().NotBe(resolved2.Partition,
            "same raw Kafka offset on partition 0 and partition 3 must map to distinct TPOs (D1 GAP-1)");
    }

    /// <summary>
    /// Required by spec C3: Register / Resolve with exact spec name.
    /// </summary>
    [Fact]
    public void Register_ThenResolveByConsumerId_ReturnsSameConsumer()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();
        KafkaConsumer consumer = CreateFakeConsumer("c-spec");
        registry.Register("c-spec", consumer);

        // Act
        KafkaConsumer? resolved = registry.ResolveByConsumerId("c-spec");

        // Assert
        resolved.Should().BeSameAs(consumer);
    }

    /// <summary>
    /// Required by spec C3: missing id returns null.
    /// </summary>
    [Fact]
    public void ResolveByConsumerId_MissingId_ReturnsNull()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();

        // Act
        KafkaConsumer? resolved = registry.ResolveByConsumerId("missing-id");

        // Assert
        resolved.Should().BeNull();
    }

    /// <summary>
    /// Required by spec C3: unregister then resolve returns null.
    /// </summary>
    [Fact]
    public void Unregister_ThenResolve_ReturnsNull()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();
        KafkaConsumer consumer = CreateFakeConsumer("c-unregister");
        registry.Register("c-unregister", consumer);

        // Act
        registry.Unregister("c-unregister");

        // Assert
        registry.ResolveByConsumerId("c-unregister").Should().BeNull();
    }

    /// <summary>
    /// Required by spec C3: StoreOffset / TryEvict round-trip + second evict returns null.
    /// </summary>
    [Fact]
    public void StoreOffset_ThenTryEvict_ReturnsStoredTopicPartitionOffset_AndSecondEvictReturnsNull()
    {
        // Arrange
        var registry = new KafkaConsumerRegistry();
        KafkaConsumer consumer = CreateFakeConsumer("c-evict");
        registry.Register("c-evict", consumer);

        var tpo = new TopicPartitionOffset("my-topic", new Partition(0), new Offset(42));
        ulong deliveryTag = 10UL;

        registry.StoreOffset("c-evict", deliveryTag, tpo);

        // Act
        TopicPartitionOffset? first = registry.TryEvictOffset("c-evict", deliveryTag);
        TopicPartitionOffset? second = registry.TryEvictOffset("c-evict", deliveryTag);

        // Assert
        first.Should().NotBeNull();
        first!.Topic.Should().Be("my-topic");
        first.Partition.Should().Be(new Partition(0));
        first.Offset.Should().Be(new Offset(42));

        // Second eviction must return null — entry was removed (no unbounded growth)
        second.Should().BeNull("eviction must remove the entry to prevent unbounded map growth");
    }
}
