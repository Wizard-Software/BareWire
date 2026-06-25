using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.RabbitMQ.Internal;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class MappingEpochCalculatorTests
{
    // ── Null / empty topology guards ──────────────────────────────────────────

    [Fact]
    public void Compute_NullTopology_ReturnsNull()
    {
        long? result = MappingEpochCalculator.Compute(null);

        result.Should().BeNull();
    }

    [Fact]
    public void Compute_NoConsistentHashExchange_ReturnsNull()
    {
        // Topology with only a Direct exchange — no consistent-hash → no epoch.
        var topology = new TopologyDeclaration
        {
            Exchanges =
            [
                new ExchangeDeclaration("my-direct-exchange", ExchangeType.Direct),
            ],
            ExchangeQueueBindings =
            [
                new ExchangeQueueBinding("my-direct-exchange", "q1", "q1"),
            ],
        };

        long? result = MappingEpochCalculator.Compute(topology);

        result.Should().BeNull();
    }

    [Fact]
    public void Compute_ConsistentHashWithNoBoundQueues_ReturnsNull()
    {
        // Consistent-hash exchange exists but no bindings reference it.
        var topology = new TopologyDeclaration
        {
            Exchanges =
            [
                new ExchangeDeclaration("ch-exchange", ExchangeType.ConsistentHash),
            ],
            ExchangeQueueBindings = [],
        };

        long? result = MappingEpochCalculator.Compute(topology);

        result.Should().BeNull();
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void Compute_SameQueueSet_ReturnsSameValue()
    {
        // Two TopologyDeclarations with the SAME bound queues declared in DIFFERENT order
        // must produce identical epochs (order-independent, deterministic).
        var topology1 = new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration("ch-ex", ExchangeType.ConsistentHash)],
            ExchangeQueueBindings =
            [
                new ExchangeQueueBinding("ch-ex", "queue-alpha", "1"),
                new ExchangeQueueBinding("ch-ex", "queue-beta", "1"),
            ],
        };

        var topology2 = new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration("ch-ex", ExchangeType.ConsistentHash)],
            ExchangeQueueBindings =
            [
                // Reversed declaration order — epoch must still match.
                new ExchangeQueueBinding("ch-ex", "queue-beta", "1"),
                new ExchangeQueueBinding("ch-ex", "queue-alpha", "1"),
            ],
        };

        long? epoch1 = MappingEpochCalculator.Compute(topology1);
        long? epoch2 = MappingEpochCalculator.Compute(topology2);

        epoch1.Should().NotBeNull();
        epoch2.Should().NotBeNull();
        epoch1.Should().Be(epoch2, "same queue set must produce the same epoch regardless of declaration order");
    }

    [Fact]
    public void Compute_DifferentQueueSet_ReturnsDifferentValue()
    {
        // Adding one bound queue changes the set → epoch must change.
        var topologySmaller = new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration("ch-ex", ExchangeType.ConsistentHash)],
            ExchangeQueueBindings =
            [
                new ExchangeQueueBinding("ch-ex", "queue-alpha", "1"),
                new ExchangeQueueBinding("ch-ex", "queue-beta", "1"),
            ],
        };

        var topologyLarger = new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration("ch-ex", ExchangeType.ConsistentHash)],
            ExchangeQueueBindings =
            [
                new ExchangeQueueBinding("ch-ex", "queue-alpha", "1"),
                new ExchangeQueueBinding("ch-ex", "queue-beta", "1"),
                new ExchangeQueueBinding("ch-ex", "queue-gamma", "1"),
            ],
        };

        long? epochSmaller = MappingEpochCalculator.Compute(topologySmaller);
        long? epochLarger = MappingEpochCalculator.Compute(topologyLarger);

        epochSmaller.Should().NotBeNull();
        epochLarger.Should().NotBeNull();
        epochLarger.Should().NotBe(epochSmaller, "adding a bound queue must change the epoch (re-map signal)");
    }

    // ── Scope: only consistent-hash bound queues contribute ───────────────────

    [Fact]
    public void Compute_IgnoresQueuesBoundToNonConsistentHashExchange()
    {
        // A queue bound to a Direct exchange must not affect the epoch.
        // Baseline: consistent-hash exchange with one bound queue.
        var baseTopology = new TopologyDeclaration
        {
            Exchanges =
            [
                new ExchangeDeclaration("ch-ex", ExchangeType.ConsistentHash),
                new ExchangeDeclaration("direct-ex", ExchangeType.Direct),
            ],
            ExchangeQueueBindings =
            [
                new ExchangeQueueBinding("ch-ex", "ch-queue", "1"),
            ],
        };

        // Extended: same consistent-hash binding PLUS a new queue bound only to the Direct exchange.
        var extendedTopology = new TopologyDeclaration
        {
            Exchanges =
            [
                new ExchangeDeclaration("ch-ex", ExchangeType.ConsistentHash),
                new ExchangeDeclaration("direct-ex", ExchangeType.Direct),
            ],
            ExchangeQueueBindings =
            [
                new ExchangeQueueBinding("ch-ex", "ch-queue", "1"),
                // This binding is to the Direct exchange — must NOT change the CH epoch.
                new ExchangeQueueBinding("direct-ex", "direct-queue", "direct-queue"),
            ],
        };

        long? epochBase = MappingEpochCalculator.Compute(baseTopology);
        long? epochExtended = MappingEpochCalculator.Compute(extendedTopology);

        epochBase.Should().NotBeNull();
        epochExtended.Should().NotBeNull();
        epochExtended.Should().Be(
            epochBase,
            "queues bound to non-consistent-hash exchanges must not affect the mapping epoch");
    }
}
