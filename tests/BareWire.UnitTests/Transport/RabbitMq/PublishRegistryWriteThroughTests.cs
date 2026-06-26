using System.Reflection;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// 16.4 — Shared write-through store (Option 1). Proves the per-type publish maps are a single
/// source of truth shared BY REFERENCE between <see cref="RabbitMqConfigurator"/> and the
/// lazily-created topology configurator: every write path (MapExchange/MapRoutingKey,
/// DeclareExchange&lt;T&gt;, Publish&lt;T&gt;) lands in ONE map set, with no parallel dictionary and
/// no merge step in Build(). Observable ordering is last-call-wins by source order.
/// </summary>
public sealed class PublishRegistryWriteThroughTests
{
    private sealed record Foo(string Value);

    private static RabbitMqConfigurator CreateConfigurator()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        return configurator;
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        FieldInfo field = instance.GetType()
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (T)field.GetValue(instance)!;
    }

    // ── SingleSourceOfTruth: configurator + topology share the SAME registry instance ────────

    [Fact]
    public void ConfigureTopology_WiresTheSamePublishRegistryInstanceIntoTopologyConfigurator()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange("users", ExchangeType.Topic));

        // Act
        var configuratorRegistry = GetPrivateField<PublishRegistry>(configurator, "_publishRegistry");
        var topology = GetPrivateField<RabbitMqTopologyConfigurator>(configurator, "_topologyConfigurator");
        var topologyRegistry = GetPrivateField<PublishRegistry>(topology, "_publishRegistry");

        // Assert — same live object, not a copy.
        topologyRegistry.Should().BeSameAs(configuratorRegistry);
    }

    // ── NoParallelDictionary: the configurator holds exactly one registry, no loose maps ─────

    [Fact]
    public void RabbitMqConfigurator_HoldsSinglePublishRegistry_AndNoParallelTypeStringDictionaries()
    {
        // Arrange
        FieldInfo[] fields = typeof(RabbitMqConfigurator)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance);

        // Assert — one shared store, and no rogue Dictionary<Type, string> field beside it.
        fields.Count(f => f.FieldType == typeof(PublishRegistry)).Should().Be(1);
        fields.Where(f => f.FieldType == typeof(Dictionary<Type, string>)).Should().BeEmpty();
    }

    // ── WriteThrough: DeclareExchange<T> is visible to the Build() snapshot (no merge step) ──

    [Fact]
    public void DeclareExchangeGeneric_WriteIsVisibleToBuildSnapshot_WithoutMergeStep()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
            t.DeclareExchange<Foo>("users", ExchangeType.Topic, routingKey: "user.created"));

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert — the mapping flows through the same store MapExchange<T> feeds.
        options.ExchangeMappings.Should().NotBeNull();
        options.ExchangeMappings![typeof(Foo)].Should().Be("users");
        options.RoutingKeyMappings.Should().NotBeNull();
        options.RoutingKeyMappings![typeof(Foo)].Should().Be("user.created");
    }

    // ── LastWins: MapExchange<T> then DeclareExchange<T> — declare wins (one map) ─────────────

    [Fact]
    public void MapExchangeThenDeclareExchangeGeneric_SameType_DeclareWins()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.MapExchange<Foo>("first");          // written first
        configurator.ConfigureTopology(t =>
        {
            t.DeclareExchange("first", ExchangeType.Topic);
            t.DeclareExchange<Foo>("second", ExchangeType.Topic); // written last
        });

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert — last write (DeclareExchange<Foo>("second")) wins; proves a single shared map.
        options.ExchangeMappings![typeof(Foo)].Should().Be("second");
    }

    // ── LastWins (reverse): DeclareExchange<T> then MapExchange<T> — MapExchange wins ─────────

    [Fact]
    public void DeclareExchangeGenericThenMapExchange_SameType_MapExchangeWins()
    {
        // Arrange
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
        {
            t.DeclareExchange<Foo>("declared", ExchangeType.Topic);
            t.DeclareExchange("mapped", ExchangeType.Topic);
        });
        configurator.MapExchange<Foo>("mapped");         // written last

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert — the later MapExchange<T> overwrites the DeclareExchange<T> mapping.
        options.ExchangeMappings![typeof(Foo)].Should().Be("mapped");
    }
}
