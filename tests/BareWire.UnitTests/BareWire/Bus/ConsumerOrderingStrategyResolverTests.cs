using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Bus;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

public sealed class ConsumerOrderingStrategyResolverTests
{
    // RabbitMQ-like capabilities: has PublisherConfirms, DlqNative, FlowControl — NO Sessions, NO OrderingKeys.
    private const TransportCapabilities RabbitMqCapabilities =
        TransportCapabilities.PublisherConfirms | TransportCapabilities.DlqNative | TransportCapabilities.FlowControl;

    // Kafka/Pub-Sub-like capabilities: has OrderingKeys.
    private const TransportCapabilities KafkaCapabilities = TransportCapabilities.OrderingKeys;

    // ASB-like capabilities: has Sessions.
    private const TransportCapabilities AsbCapabilities = TransportCapabilities.Sessions;

    private static IConsumerOrderingConfiguration MakeOrdering(
        ConsumerOrderingStrategy strategy,
        TransportAffinity affinity = TransportAffinity.None,
        string headerName = "X-Order-Key")
    {
        IConsumerOrderingConfiguration ordering = Substitute.For<IConsumerOrderingConfiguration>();
        ordering.Strategy.Returns(strategy);
        ordering.TransportAffinity.Returns(affinity);
        ordering.HeaderName.Returns(headerName);
        return ordering;
    }

    // Test #1: Kafka/Pub-Sub transport with OrderingKeys — Auto picks TransportNative.
    [Fact]
    public void Resolve_AutoOnTransportWithOrderingKeys_SelectsTransportNative()
    {
        IConsumerOrderingConfiguration ordering = MakeOrdering(ConsumerOrderingStrategy.Auto);

        ResolvedConsumerOrdering result = ConsumerOrderingStrategyResolver.Resolve(
            ordering, KafkaCapabilities, "kafka", "orders");

        result.EffectiveStrategy.Should().Be(ConsumerOrderingStrategy.TransportNative);
        result.EffectiveAffinity.Should().Be(TransportAffinity.None);
    }

    // Test #2: RabbitMQ with ConsistentHash affinity — Auto picks TransportNative with ConsistentHash.
    [Fact]
    public void Resolve_AutoOnRabbitMqWithConsistentHashAffinity_SelectsTransportNative()
    {
        IConsumerOrderingConfiguration ordering = MakeOrdering(
            ConsumerOrderingStrategy.Auto, TransportAffinity.ConsistentHash);

        ResolvedConsumerOrdering result = ConsumerOrderingStrategyResolver.Resolve(
            ordering, RabbitMqCapabilities, "rabbitmq", "orders");

        result.EffectiveStrategy.Should().Be(ConsumerOrderingStrategy.TransportNative);
        result.EffectiveAffinity.Should().Be(TransportAffinity.ConsistentHash);
    }

    // Test #3: RabbitMQ with SingleActiveConsumer affinity — Auto picks TransportNative with SAC.
    [Fact]
    public void Resolve_AutoOnRabbitMqWithSingleActiveConsumerAffinity_SelectsTransportNative()
    {
        IConsumerOrderingConfiguration ordering = MakeOrdering(
            ConsumerOrderingStrategy.Auto, TransportAffinity.SingleActiveConsumer);

        ResolvedConsumerOrdering result = ConsumerOrderingStrategyResolver.Resolve(
            ordering, RabbitMqCapabilities, "rabbitmq", "payments");

        result.EffectiveStrategy.Should().Be(ConsumerOrderingStrategy.TransportNative);
        result.EffectiveAffinity.Should().Be(TransportAffinity.SingleActiveConsumer);
    }

    // Test #4: RabbitMQ without any declared affinity — Auto fails fast.
    [Fact]
    public void Resolve_AutoOnRabbitMqWithoutDeclaredAffinity_Throws()
    {
        IConsumerOrderingConfiguration ordering = MakeOrdering(
            ConsumerOrderingStrategy.Auto, TransportAffinity.None);

        Action act = () => ConsumerOrderingStrategyResolver.Resolve(
            ordering, RabbitMqCapabilities, "rabbitmq", "notifications");

        act.Should().Throw<BareWireConfigurationException>();
    }

    // Test #5: ASB Sessions capability → fail-fast for both Auto and TransportNative (M2 gate, D1).
    [Theory]
    [InlineData(ConsumerOrderingStrategy.Auto)]
    [InlineData(ConsumerOrderingStrategy.TransportNative)]
    public void Resolve_AutoOnAsbSessionsBeforeR22_Throws(ConsumerOrderingStrategy strategy)
    {
        IConsumerOrderingConfiguration ordering = MakeOrdering(strategy);

        var ex = Assert.Throws<BareWireConfigurationException>(() =>
            ConsumerOrderingStrategyResolver.Resolve(
                ordering, AsbCapabilities, "azureservicebus", "invoice-events"));

        ex.Message.Should().Contain("R2.2",
            because: "the Core M2 gate must reference R2.2 so operators know when this will be resolved");
    }

    // Test #6: TransportNative with declarative affinity on Kafka (OrderingKeys) — contradiction → throws (D4).
    [Fact]
    public void Resolve_TransportNativeContradictsCapability_Throws()
    {
        IConsumerOrderingConfiguration ordering = MakeOrdering(
            ConsumerOrderingStrategy.TransportNative, TransportAffinity.SingleActiveConsumer);

        Action act = () => ConsumerOrderingStrategyResolver.Resolve(
            ordering, KafkaCapabilities, "kafka", "shipments");

        act.Should().Throw<BareWireConfigurationException>();
    }

    // Test #7: LocalPartitioned never throws and is always passed through unchanged.
    [Theory]
    [InlineData(TransportCapabilities.None, TransportAffinity.None)]
    [InlineData(TransportCapabilities.Sessions, TransportAffinity.None)]
    [InlineData(TransportCapabilities.OrderingKeys, TransportAffinity.ConsistentHash)]
    [InlineData(RabbitMqCapabilities, TransportAffinity.SingleActiveConsumer)]
    public void Resolve_LocalPartitioned_NeverThrowsAndIsPassedThrough(
        TransportCapabilities capabilities, TransportAffinity affinity)
    {
        IConsumerOrderingConfiguration ordering = MakeOrdering(
            ConsumerOrderingStrategy.LocalPartitioned, affinity);

        ResolvedConsumerOrdering result = ConsumerOrderingStrategyResolver.Resolve(
            ordering, capabilities, "any-transport", "any-endpoint");

        result.EffectiveStrategy.Should().Be(ConsumerOrderingStrategy.LocalPartitioned);
    }

    // Test #8: Auto never silently returns LocalPartitioned — when no transport path exists, it throws.
    // (ADR-026 hard invariant: Auto never selects LocalPartitioned.)
    [Fact]
    public void Resolve_AutoNeverSelectsLocalPartitioned()
    {
        // Use RabbitMQ-like caps + None affinity — a fail-fast path for Auto.
        IConsumerOrderingConfiguration ordering = MakeOrdering(
            ConsumerOrderingStrategy.Auto, TransportAffinity.None);

        // Must throw — never return LocalPartitioned silently.
        Action act = () => ConsumerOrderingStrategyResolver.Resolve(
            ordering, RabbitMqCapabilities, "rabbitmq", "audit-log");

        act.Should().Throw<BareWireConfigurationException>(
            because: "Auto must never silently fall back to LocalPartitioned — it must fail fast");
    }

    // Test #9: S1 — exception must not leak any ordering-key header value.
    // Non-vacuous: sentinel is disjoint from strategy/affinity/endpoint/transport names.
    [Fact]
    public void Resolve_Throws_DoesNotLeakKeyValue()
    {
        // Sentinel that is completely disjoint from strategy names, affinity names, transport names,
        // and endpoint names — so NotContain cannot false-pass due to coincidental match.
        const string HeaderNameSentinel = "X-Order-Key-SENTINEL";

        IConsumerOrderingConfiguration ordering = MakeOrdering(
            ConsumerOrderingStrategy.Auto,
            TransportAffinity.None,
            headerName: HeaderNameSentinel);

        // RabbitMQ-like caps + None affinity → fail-fast path (no declared ordering path).
        var ex = Assert.Throws<BareWireConfigurationException>(() =>
            ConsumerOrderingStrategyResolver.Resolve(
                ordering, RabbitMqCapabilities, "rabbitmq", "sensor-data"));

        // S1: the header name (a config-time key name) must not appear in the exception.
        ex.Message.Should().NotContain(HeaderNameSentinel,
            because: "ordering-key header names must never appear in exception messages (S1 rule)");
        ex.OptionValue.Should().NotContain(HeaderNameSentinel,
            because: "ordering-key header names must never appear in OptionValue (S1 rule)");

        // Positive assertion — OptionValue carries the strategy name so the operator knows what to fix.
        ex.OptionValue.Should().Be(ConsumerOrderingStrategy.Auto.ToString(),
            because: "OptionValue must carry the strategy name, not a key or header value");
    }
}
