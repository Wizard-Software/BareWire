using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;
using BareWire.Transport.RabbitMQ.Internal;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// 16.5 — Generic <c>DeclareExchange&lt;T&gt;</c> ("declare + map"). Verifies the dual
/// responsibility: (1) declares the exchange in topology exactly like the non-generic overload,
/// and (2) write-through registers the per-type mapping into the shared store — exchange always,
/// routing key only when supplied (null preserves the <c>typeof(T).FullName</c> fallback).
/// </summary>
public sealed class RabbitMqTopologyConfiguratorDeclareExchangeGenericTests
{
    private sealed record UserCreated(string UserId);

    private static RabbitMqConfigurator CreateConfigurator()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        return configurator;
    }

    // ── DeclareExchange<T> with routingKey → exchange declared + mapped + routing key mapped ──

    [Fact]
    public void DeclareExchangeGeneric_WithRoutingKey_DeclaresExchangeAndMapsExchangeAndRoutingKey()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
            t.DeclareExchange<UserCreated>("users", ExchangeType.Topic, routingKey: "user.created"));

        // Act
        RabbitMqTransportOptions options = configurator.Build();
        var exchangeResolver = new ExchangeResolver(options.ExchangeMappings);
        var routingKeyResolver = new RoutingKeyResolver(options.RoutingKeyMappings);

        // Assert — (1) exchange declared in topology.
        options.Topology.Should().NotBeNull();
        options.Topology!.Exchanges.Should().ContainSingle(e => e.Name == "users" && e.Type == ExchangeType.Topic);

        // Assert — (2) resolvers see exchange = "users", routing key = "user.created".
        exchangeResolver.Resolve<UserCreated>().Should().Be("users");
        routingKeyResolver.Resolve<UserCreated>().Should().Be("user.created");
    }

    // ── DeclareExchange<T> without routingKey → exchange mapped, routing key = FullName fallback ─

    [Fact]
    public void DeclareExchangeGeneric_WithoutRoutingKey_MapsExchangeAndLeavesRoutingKeyFallback()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
            t.DeclareExchange<UserCreated>("users", ExchangeType.Topic));

        // Act
        RabbitMqTransportOptions options = configurator.Build();
        var exchangeResolver = new ExchangeResolver(options.ExchangeMappings);
        var routingKeyResolver = new RoutingKeyResolver(options.RoutingKeyMappings);

        // Assert — exchange mapped; no routing-key mapping registered.
        exchangeResolver.Resolve<UserCreated>().Should().Be("users");
        options.RoutingKeyMappings.Should().BeNull(); // no routing-key writes at all → snapshot stays null
        routingKeyResolver.Resolve<UserCreated>().Should().Be(typeof(UserCreated).FullName);
    }

    // ── DeclareExchange<T> null routingKey must NOT overwrite an existing routing-key mapping ──

    [Fact]
    public void DeclareExchangeGeneric_WithNullRoutingKey_DoesNotOverwriteExistingRoutingKey()
    {
        // Arrange — an explicit routing key is set first, then declared without one.
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.MapRoutingKey<UserCreated>("explicit.key");
        configurator.ConfigureTopology(t =>
            t.DeclareExchange<UserCreated>("users", ExchangeType.Topic));

        // Act
        RabbitMqTransportOptions options = configurator.Build();
        var routingKeyResolver = new RoutingKeyResolver(options.RoutingKeyMappings);

        // Assert — the prior explicit routing key survives (null = no overwrite).
        routingKeyResolver.Resolve<UserCreated>().Should().Be("explicit.key");
    }

    // ── Name guard parity with the non-generic overload ───────────────────────────────────────

    [Fact]
    public void DeclareExchangeGeneric_WithNullName_ThrowsArgumentException()
    {
        // Arrange
        var sut = new RabbitMqTopologyConfigurator();

        // Act
        Action act = () => sut.DeclareExchange<UserCreated>(null!, ExchangeType.Topic);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DeclareExchangeGeneric_WithEmptyName_ThrowsArgumentException()
    {
        // Arrange
        var sut = new RabbitMqTopologyConfigurator();

        // Act
        Action act = () => sut.DeclareExchange<UserCreated>(string.Empty, ExchangeType.Topic);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
