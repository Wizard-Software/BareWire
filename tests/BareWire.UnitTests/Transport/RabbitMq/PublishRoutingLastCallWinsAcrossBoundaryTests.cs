using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;
using BareWire.Transport.RabbitMQ.Internal;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// 16.10 — Last-call-wins-by-source-order ACROSS the topology↔root configurator boundary. Because the
/// topology configurator (<c>DeclareExchange&lt;T&gt;</c>) and the root configurator
/// (<c>MapExchange&lt;T&gt;</c>/<c>MapRoutingKey&lt;T&gt;</c>/<c>Publish&lt;T&gt;</c>) share ONE
/// <see cref="PublishRegistry"/> instance by reference, the later write always wins regardless of which
/// side performed it. These tests would FAIL if the two paths used separate dictionaries.
/// </summary>
public sealed class PublishRoutingLastCallWinsAcrossBoundaryTests
{
    private sealed record Foo(string Value);

    private static RabbitMqConfigurator CreateConfigurator()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        return configurator;
    }

    private static ExchangeResolver ExchangeResolverFor(RabbitMqTransportOptions options) =>
        new(options.ExchangeMappings);

    private static RoutingKeyResolver RoutingKeyResolverFor(RabbitMqTransportOptions options) =>
        new(options.RoutingKeyMappings);

    // ── Exchange (a): DeclareExchange<T> first, then Publish<T> — Publish wins ─────────────────

    [Fact]
    public void DeclareExchangeGenericThenPublish_SameType_ResolverResolvesPublishExchange()
    {
        // Arrange — declare "b" (so it is valid) and DeclareExchange<Foo>("a"); then Publish<Foo>("b").
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
        {
            t.DeclareExchange("b", ExchangeType.Topic);
            t.DeclareExchange<Foo>("a", ExchangeType.Topic);
        });
        configurator.Publish<Foo>(p => p.Exchange("b")); // written last

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert — last write (Publish on the root configurator) wins.
        ExchangeResolverFor(options).Resolve<Foo>().Should().Be("b");
    }

    // ── Exchange (b): Publish<T> first, then DeclareExchange<T> — DeclareExchange wins ────────

    [Fact]
    public void PublishThenDeclareExchangeGeneric_SameType_ResolverResolvesDeclaredExchange()
    {
        // Arrange — declare "b" first; Publish<Foo>("b"); then DeclareExchange<Foo>("a") (declares + maps).
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange("b", ExchangeType.Topic));
        configurator.Publish<Foo>(p => p.Exchange("b"));
        configurator.ConfigureTopology(t => t.DeclareExchange<Foo>("a", ExchangeType.Topic)); // written last

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert — last write (DeclareExchange<T> on the topology configurator) wins.
        ExchangeResolverFor(options).Resolve<Foo>().Should().Be("a");
    }

    // ── RoutingKey (a): DeclareExchange<T>(routingKey:) first, then MapRoutingKey<T> — Map wins ─

    [Fact]
    public void DeclareExchangeGenericRoutingKeyThenMapRoutingKey_SameType_ResolverResolvesMapped()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
            t.DeclareExchange<Foo>("x", ExchangeType.Topic, routingKey: "rk-a"));
        configurator.MapRoutingKey<Foo>("rk-b"); // written last

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        RoutingKeyResolverFor(options).Resolve<Foo>().Should().Be("rk-b");
    }

    // ── RoutingKey (b): MapRoutingKey<T> first, then DeclareExchange<T>(routingKey:) — Declare wins ─

    [Fact]
    public void MapRoutingKeyThenDeclareExchangeGenericRoutingKey_SameType_ResolverResolvesDeclared()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.MapRoutingKey<Foo>("rk-b");
        configurator.ConfigureTopology(t =>
            t.DeclareExchange<Foo>("x", ExchangeType.Topic, routingKey: "rk-a")); // written last

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        RoutingKeyResolverFor(options).Resolve<Foo>().Should().Be("rk-a");
    }

    // ── Mixed: MapExchange<T> (root) then Publish<T> (root) — Publish wins; cross-checks one map ─

    [Fact]
    public void MapExchangeThenPublish_SameType_ResolverResolvesPublishExchange()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
        {
            t.DeclareExchange("a", ExchangeType.Topic);
            t.DeclareExchange("b", ExchangeType.Topic);
        });
        configurator.MapExchange<Foo>("a");
        configurator.Publish<Foo>(p => p.Exchange("b")); // written last

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        ExchangeResolverFor(options).Resolve<Foo>().Should().Be("b");
    }
}
