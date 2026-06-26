using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// 16.9 — Fail-fast for <c>Publish&lt;T&gt;(p =&gt; p.Exchange(undeclared))</c>. Because the grouped
/// <c>Publish&lt;T&gt;</c> block writes THROUGH to the same per-type exchange map that
/// <c>MapExchange&lt;T&gt;</c> feeds, the existing <c>ValidateExchangeMappings</c> in <c>Build()</c>
/// already rejects an undeclared exchange — same <see cref="BareWireConfigurationException"/>, same
/// <c>OptionName</c> ("MapExchange"), no separate validation path. <c>DeclareExchange&lt;T&gt;</c> is
/// self-consistent (it declares the exchange it maps), so it never trips this check.
/// </summary>
public sealed class RabbitMqConfiguratorPublishExchangeValidationTests
{
    private sealed record Foo(string Value);

    private static RabbitMqConfigurator CreateConfigurator()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        return configurator;
    }

    // ── Publish<T>.Exchange(undeclared) with a declared topology → same exception as MapExchange ─

    [Fact]
    public void Build_WhenPublishExchangeNotInTopology_ThrowsConfigurationException_SameAsMapExchange()
    {
        // Arrange — topology declares only "orders.direct"; Publish maps Foo to a missing exchange.
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange("orders.direct", ExchangeType.Direct));
        configurator.Publish<Foo>(p => p.Exchange("payments.topic"));

        // Act
        Action act = () => configurator.Build();

        // Assert — identical type / OptionName / OptionValue to the MapExchange<T> failure path.
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Be("MapExchange");
        ex.OptionValue.Should().Contain("payments.topic");
    }

    // ── Publish<T>.Exchange(undeclared) with NO topology at all → topology-null branch ────────

    [Fact]
    public void Build_WhenPublishExchangeButNoTopology_ThrowsConfigurationException()
    {
        // Arrange — no ConfigureTopology call at all.
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.Publish<Foo>(p => p.Exchange("payments.topic"));

        // Act
        Action act = () => configurator.Build();

        // Assert — same validator (topology-null branch) used by MapExchange<T>.
        BareWireConfigurationException ex = act.Should().Throw<BareWireConfigurationException>().Which;
        ex.OptionName.Should().Be("MapExchange");
    }

    // ── Parity: MapExchange<T>(undeclared) throws the SAME exception OptionName ───────────────

    [Fact]
    public void Build_PublishAndMapExchange_UndeclaredExchange_ThrowSameOptionName()
    {
        // Arrange — two configurators that differ only in the registration shape.
        RabbitMqConfigurator viaPublish = CreateConfigurator();
        viaPublish.ConfigureTopology(t => t.DeclareExchange("declared", ExchangeType.Topic));
        viaPublish.Publish<Foo>(p => p.Exchange("missing"));

        RabbitMqConfigurator viaMapExchange = CreateConfigurator();
        viaMapExchange.ConfigureTopology(t => t.DeclareExchange("declared", ExchangeType.Topic));
        viaMapExchange.MapExchange<Foo>("missing");

        // Act
        BareWireConfigurationException publishEx =
            ((Action)(() => viaPublish.Build())).Should().Throw<BareWireConfigurationException>().Which;
        BareWireConfigurationException mapEx =
            ((Action)(() => viaMapExchange.Build())).Should().Throw<BareWireConfigurationException>().Which;

        // Assert — both feed the SAME validated map, so the failure is indistinguishable by shape.
        publishEx.OptionName.Should().Be(mapEx.OptionName);
        publishEx.OptionValue.Should().Contain("missing");
        mapEx.OptionValue.Should().Contain("missing");
    }

    // ── DeclareExchange<T> is self-consistent → never throws for an undeclared exchange ───────

    [Fact]
    public void Build_DeclareExchangeGeneric_NeverThrowsForUndeclaredExchange()
    {
        // Arrange — DeclareExchange<T> declares the exchange it maps; the auto-mapping always passes.
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
            t.DeclareExchange<Foo>("self.declared", ExchangeType.Topic));

        // Act
        Action act = () => configurator.Build();

        // Assert — no validation failure; the mapping points at an exchange it just declared.
        act.Should().NotThrow();
        RabbitMqTransportOptions options = configurator.Build();
        options.ExchangeMappings![typeof(Foo)].Should().Be("self.declared");
    }
}
