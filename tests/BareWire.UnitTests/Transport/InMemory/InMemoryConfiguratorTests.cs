using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryConfiguratorTests
{
    [Fact]
    public void Build_WithNoCalls_ReturnsDefaults()
    {
        InMemoryTransportOptions o = new InMemoryConfigurator().Build();
        o.QueueCapacity.Should().Be(1_000);
        o.Topology.Should().BeNull();
        o.EndpointConfigurations.Should().BeEmpty();
        o.DefaultExchange.Should().BeNull();
    }

    [Fact]
    public void Build_AfterScalarSetters_CapturesAllValues()
    {
        var c = new InMemoryConfigurator();
        c.QueueCapacity(250);
        c.SendTimeout(TimeSpan.Zero);
        c.MaxMessageSize(1024);
        c.MaxRedeliveries(3);
        c.DrainTimeout(TimeSpan.FromSeconds(2));
        c.EnableDefer(TimeSpan.FromSeconds(5));
        c.GuaranteedRouting();
        c.AutoDeclareEndpointQueues();
        c.DefaultExchange(string.Empty);

        InMemoryTransportOptions o = c.Build();
        o.QueueCapacity.Should().Be(250);
        o.SendTimeout.Should().Be(TimeSpan.Zero);
        o.MaxMessageSize.Should().Be(1024);
        o.MaxRedeliveries.Should().Be(3);
        o.DrainTimeout.Should().Be(TimeSpan.FromSeconds(2));
        o.DeferEnabled.Should().BeTrue();
        o.DeferDelay.Should().Be(TimeSpan.FromSeconds(5));
        o.GuaranteedRouting.Should().BeTrue();
        o.AutoDeclareEndpointQueues.Should().BeTrue();
        o.DefaultExchange.Should().Be(string.Empty);
    }

    [Fact]
    public void EnableDefer_WithNullDelay_UsesThirtySecondDefault()
    {
        var c = new InMemoryConfigurator();
        c.EnableDefer();

        InMemoryTransportOptions o = c.Build();
        o.DeferEnabled.Should().BeTrue();
        o.DeferDelay.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void QueueCapacity_WithZero_DoesNotThrowUntilBuild()
    {
        var c = new InMemoryConfigurator();
        c.Invoking(x => x.QueueCapacity(0)).Should().NotThrow();
        c.Invoking(x => x.Build()).Should().Throw<BareWireConfigurationException>();
    }

    [Fact]
    public void ConfigureTopology_DeclaredExchangeQueueAndBinding_AppearInTopology()
    {
        var c = new InMemoryConfigurator();
        c.ConfigureTopology(t =>
        {
            t.DeclareExchange("orders", ExchangeType.Topic);
            t.DeclareQueue("order-processing");
            t.BindExchangeToQueue("orders", "order-processing", "order.*");
        });

        TopologyDeclaration topology = c.Build().Topology!;
        topology.Exchanges.Should().ContainSingle(e => e.Name == "orders");
        topology.Queues.Should().ContainSingle(q => q.Name == "order-processing");
        topology.ExchangeQueueBindings.Should().ContainSingle();
    }

    [Fact]
    public void ReceiveEndpoint_WithConsumer_CapturesEndpointAndSettings()
    {
        var c = new InMemoryConfigurator();
        c.ReceiveEndpoint("q", e =>
        {
            e.PrefetchCount = 4;
            e.Consumer<TestConsumer, OrderCreated>();
        });

        InMemoryTransportOptions o = c.Build();
        o.EndpointConfigurations.Should().ContainSingle();
        InMemoryEndpointConfiguration endpoint = o.EndpointConfigurations[0];
        endpoint.QueueName.Should().Be("q");
        endpoint.PrefetchCount.Should().Be(4);
        endpoint.ConsumerRegistrations.Should().ContainSingle();
    }

    [Fact]
    public void Publish_ThenMapExchange_LastCallWins()
    {
        var c = new InMemoryConfigurator();
        c.Publish<OrderCreated>(p => p.Exchange("a"));
        c.MapExchange<OrderCreated>("b");

        InMemoryTransportOptions o = c.Build();
        o.ExchangeMappings[typeof(OrderCreated)].Should().Be("b");
    }

    [Fact]
    public void MapRoutingKey_CapturesMapping()
    {
        var c = new InMemoryConfigurator();
        c.MapRoutingKey<OrderCreated>("order.created");

        InMemoryTransportOptions o = c.Build();
        o.RoutingKeyMappings[typeof(OrderCreated)].Should().Be("order.created");
    }

    [Fact]
    public void ConfigureTopology_WithNull_ThrowsArgumentNullException()
    {
        var c = new InMemoryConfigurator();
        c.Invoking(x => x.ConfigureTopology(null!)).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ReceiveEndpoint_WithNullOrEmptyQueueName_ThrowsArgumentException(string? queueName)
    {
        var c = new InMemoryConfigurator();
        c.Invoking(x => x.ReceiveEndpoint(queueName!, _ => { })).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReceiveEndpoint_WithNullConfigure_ThrowsArgumentNullException()
    {
        var c = new InMemoryConfigurator();
        c.Invoking(x => x.ReceiveEndpoint("q", null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DefaultExchange_WithNull_ThrowsArgumentNullException()
    {
        var c = new InMemoryConfigurator();
        c.Invoking(x => x.DefaultExchange(null!)).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MapExchange_WithNullOrEmptyName_ThrowsArgumentException(string? exchangeName)
    {
        var c = new InMemoryConfigurator();
        c.Invoking(x => x.MapExchange<OrderCreated>(exchangeName!)).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MapRoutingKey_WithNullOrEmptyKey_ThrowsArgumentException(string? routingKey)
    {
        var c = new InMemoryConfigurator();
        c.Invoking(x => x.MapRoutingKey<OrderCreated>(routingKey!)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Publish_WithNullConfigure_ThrowsArgumentNullException()
    {
        var c = new InMemoryConfigurator();
        c.Invoking(x => x.Publish<OrderCreated>(null!)).Should().Throw<ArgumentNullException>();
    }

    private sealed record OrderCreated(Guid Id);

    private sealed class TestConsumer : IConsumer<OrderCreated>
    {
        public Task ConsumeAsync(ConsumeContext<OrderCreated> context) => Task.CompletedTask;
    }
}
