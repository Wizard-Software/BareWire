using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;
using BareWire.Transport.RabbitMQ.Internal;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// 16.6 — Grouped <c>Publish&lt;T&gt;</c> block and its <see cref="PublishConfigurator{T}"/>.
/// Verifies <c>Exchange</c>/<c>RoutingKey</c> write through to the shared store, last-call-wins
/// per <c>T</c> across shapes, argument guards, and the internal-sealed-in-Configuration contract.
/// </summary>
public sealed class RabbitMqConfiguratorPublishTests
{
    private sealed record Foo(string Value);

    private static RabbitMqConfigurator CreateConfigurator()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        return configurator;
    }

    // ── Publish<T> sets both exchange and routing key → resolvers see them ────────────────────

    [Fact]
    public void Publish_WithExchangeAndRoutingKey_ResolversSeeBoth()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange("b", ExchangeType.Topic));
        configurator.Publish<Foo>(p =>
        {
            p.Exchange("b");
            p.RoutingKey("y");
        });

        // Act
        RabbitMqTransportOptions options = configurator.Build();
        var exchangeResolver = new ExchangeResolver(options.ExchangeMappings);
        var routingKeyResolver = new RoutingKeyResolver(options.RoutingKeyMappings);

        // Assert
        exchangeResolver.Resolve<Foo>().Should().Be("b");
        routingKeyResolver.Resolve<Foo>().Should().Be("y");
    }

    // ── Publish<T> feeds the SAME store as MapExchange<T>; last call wins ─────────────────────

    [Fact]
    public void MapExchangeThenPublish_SameType_PublishExchangeWins()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
        {
            t.DeclareExchange("a", ExchangeType.Topic);
            t.DeclareExchange("b", ExchangeType.Topic);
        });
        configurator.MapExchange<Foo>("a");                 // written first
        configurator.Publish<Foo>(p => p.Exchange("b"));    // written last

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert — single shared map, last write wins.
        options.ExchangeMappings![typeof(Foo)].Should().Be("b");
    }

    // ── Argument guards ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Publish_WithNullConfigure_ThrowsArgumentNullException()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();

        // Act
        Action act = () => configurator.Publish<Foo>(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Publish_ExchangeWithNullOrEmpty_ThrowsArgumentException()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();

        // Act
        Action withNull = () => configurator.Publish<Foo>(p => p.Exchange(null!));
        Action withEmpty = () => configurator.Publish<Foo>(p => p.Exchange(string.Empty));

        // Assert
        withNull.Should().Throw<ArgumentException>();
        withEmpty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Publish_RoutingKeyWithNullOrEmpty_ThrowsArgumentException()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();

        // Act
        Action withNull = () => configurator.Publish<Foo>(p => p.RoutingKey(null!));
        Action withEmpty = () => configurator.Publish<Foo>(p => p.RoutingKey(string.Empty));

        // Assert
        withNull.Should().Throw<ArgumentException>();
        withEmpty.Should().Throw<ArgumentException>();
    }

    // ── PublishConfigurator<T> contract: internal, sealed, in Configuration namespace ─────────

    [Fact]
    public void PublishConfigurator_IsInternalSealed_InConfigurationNamespace()
    {
        // Arrange
        Type type = typeof(PublishConfigurator<>);

        // Assert
        type.IsSealed.Should().BeTrue();
        type.IsPublic.Should().BeFalse();
        type.IsNotPublic.Should().BeTrue();
        type.Namespace.Should().Be("BareWire.Transport.RabbitMQ.Configuration");
    }
}
