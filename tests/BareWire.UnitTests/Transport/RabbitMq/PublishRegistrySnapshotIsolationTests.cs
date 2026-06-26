using System.Reflection;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;
using BareWire.Transport.RabbitMQ.Internal;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// 16.7 — SEC-4 snapshot invariant. Proves that <c>Build()</c> hands the runtime resolvers a defensive
/// COPY of the per-type publish-routing maps, never the live config-time <see cref="PublishRegistry"/>
/// dictionaries. Mutating the shared registry after <c>Build()</c> must not reach into the snapshot the
/// resolvers consume, and the snapshot dictionary instance must not be reference-equal to the live one.
/// </summary>
public sealed class PublishRegistrySnapshotIsolationTests
{
    private sealed record Foo(string Value);
    private sealed record Bar(string Value);

    private static RabbitMqConfigurator CreateConfigurator()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        return configurator;
    }

    private static PublishRegistry GetLiveRegistry(RabbitMqConfigurator configurator)
    {
        FieldInfo field = typeof(RabbitMqConfigurator)
            .GetField("_publishRegistry", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (PublishRegistry)field.GetValue(configurator)!;
    }

    // ── ExchangeMappings snapshot is a distinct instance from the live registry dictionary ────

    [Fact]
    public void Build_ExchangeMappings_IsNotReferenceEqualToLiveRegistryDictionary()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange("orders", ExchangeType.Topic));
        configurator.MapExchange<Foo>("orders");

        // Act
        RabbitMqTransportOptions options = configurator.Build();
        PublishRegistry live = GetLiveRegistry(configurator);

        // Assert — the runtime dictionary is a snapshot, NOT the live config-time instance.
        options.ExchangeMappings.Should().NotBeNull();
        ReferenceEquals(options.ExchangeMappings, live.ExchangeMappings).Should().BeFalse();
    }

    // ── RoutingKeyMappings snapshot is a distinct instance from the live registry dictionary ──

    [Fact]
    public void Build_RoutingKeyMappings_IsNotReferenceEqualToLiveRegistryDictionary()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.MapRoutingKey<Foo>("orders.created");

        // Act
        RabbitMqTransportOptions options = configurator.Build();
        PublishRegistry live = GetLiveRegistry(configurator);

        // Assert
        options.RoutingKeyMappings.Should().NotBeNull();
        ReferenceEquals(options.RoutingKeyMappings, live.RoutingKeyMappings).Should().BeFalse();
    }

    // ── Mutating the live registry's exchange map AFTER Build() does not change resolver output ─

    [Fact]
    public void Build_ThenMutatingLiveRegistryExchange_DoesNotChangeResolverOutput()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange("orders", ExchangeType.Topic));
        configurator.MapExchange<Foo>("orders");
        RabbitMqTransportOptions options = configurator.Build();

        var resolver = new ExchangeResolver(options.ExchangeMappings);
        resolver.Resolve<Foo>().Should().Be("orders"); // baseline

        // Act — mutate the LIVE config-time registry after the snapshot was taken.
        PublishRegistry live = GetLiveRegistry(configurator);
        live.MapExchange(typeof(Foo), "hijacked");   // overwrite an existing mapping
        live.MapExchange(typeof(Bar), "added-later"); // add a brand-new mapping

        // Assert — the snapshot and the resolver built from it are unaffected.
        resolver.Resolve<Foo>().Should().Be("orders");
        resolver.Resolve<Bar>().Should().BeNull();
        options.ExchangeMappings![typeof(Foo)].Should().Be("orders");
        options.ExchangeMappings!.ContainsKey(typeof(Bar)).Should().BeFalse();
    }

    // ── Mutating the live registry's routing-key map AFTER Build() does not change resolver output ─

    [Fact]
    public void Build_ThenMutatingLiveRegistryRoutingKey_DoesNotChangeResolverOutput()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.MapRoutingKey<Foo>("orders.created");
        RabbitMqTransportOptions options = configurator.Build();

        var resolver = new RoutingKeyResolver(options.RoutingKeyMappings);
        resolver.Resolve<Foo>().Should().Be("orders.created"); // baseline

        // Act — mutate the LIVE config-time registry after the snapshot was taken.
        PublishRegistry live = GetLiveRegistry(configurator);
        live.MapRoutingKey(typeof(Foo), "hijacked.key");

        // Assert — resolver still serves the snapshotted value.
        resolver.Resolve<Foo>().Should().Be("orders.created");
        options.RoutingKeyMappings![typeof(Foo)].Should().Be("orders.created");
    }
}
