using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Tests for the OPT-IN AMQP topology helper <see cref="ConsumerConfiguratorTopologyExtensions.DeclareTopology{TConsumer, TMessage}"/>
/// and the internal accumulation it drives on <c>ConsumerConfigurator&lt;,&gt;</c>. Asserts the four hard
/// invariants: (a) opt-in — without the call no broker entity is created; (b) the declared entities flow
/// through the transport seam into <c>RabbitMqTransportOptions.Topology</c> (the adapter's DeployTopologyAsync
/// path); (c) the AMQP binding key is a separate axis from the consumer's dispatcher routing keys; and (I-2)
/// the helper is an extension method in the transport assembly, never a member of the zero-dependency
/// <see cref="IConsumerConfigurator{TConsumer, TMessage}"/> façade.
/// </summary>
public sealed class ConsumerConfiguratorTopologyExtensionsTests
{
    [Fact]
    public void DeclareTopology_WhenCalled_AccumulatesOneExchangeOneQueueOneBinding()
    {
        ConsumerConfigurator<StubConsumer, StubMessage> cfg = new();

        cfg.DeclareTopology("orders-x", "orders-q", "orders.created", ExchangeType.Topic);

        var topo = cfg.BuildTopology();
        topo.Should().NotBeNull();
        topo!.Exchanges.Should().ContainSingle(e => e.Name == "orders-x" && e.Type == ExchangeType.Topic);
        topo.Queues.Should().ContainSingle(q => q.Name == "orders-q");
        topo.ExchangeQueueBindings.Should().ContainSingle(b =>
            b.ExchangeName == "orders-x" && b.QueueName == "orders-q" && b.RoutingKey == "orders.created");
    }

    [Fact]
    public void BuildTopology_WhenDeclareTopologyNeverCalled_ReturnsNull()
    {
        ConsumerConfigurator<StubConsumer, StubMessage> cfg = new();

        cfg.RoutingKey("orders.*"); // dispatcher axis set; topology NOT declared

        cfg.BuildTopology().Should().BeNull(); // invariant (a): manual topology unchanged
    }

    [Fact]
    public void DeclareTopology_BindingKeyIndependentOfDispatcherRoutingKeys()
    {
        ConsumerConfigurator<StubConsumer, StubMessage> cfg = new();

        cfg.RoutingKey("dispatch.a");                        // dispatcher axis
        cfg.DeclareTopology("x", "q", bindingKey: "bind.b"); // binding axis — different value

        cfg.BuildTopology()!.ExchangeQueueBindings.Should()
            .ContainSingle(b => b.RoutingKey == "bind.b");   // binding = "bind.b", NOT "dispatch.a"

        ConsumerRegistration reg = cfg.Build();
        reg.RoutingKeys.Should().BeEquivalentTo(["dispatch.a"]); // dispatcher untouched by topology (invariant c)
    }

    [Fact]
    public void Build_WithConsumerDeclareTopology_MergesIntoTransportOptionsTopology()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        configurator.ReceiveEndpoint("orders-q", e =>
            e.Consumer<StubConsumer, StubMessage>(c => c.DeclareTopology("orders-x", "orders-q", "orders.created")));

        RabbitMqTransportOptions opts = configurator.Build();

        opts.Topology.Should().NotBeNull();
        opts.Topology!.Exchanges.Should().ContainSingle(x => x.Name == "orders-x");
        opts.Topology.Queues.Should().ContainSingle(q => q.Name == "orders-q");
        opts.Topology.ExchangeQueueBindings.Should().ContainSingle(b =>
            b.ExchangeName == "orders-x" && b.QueueName == "orders-q" && b.RoutingKey == "orders.created");
    }

    [Fact]
    public void Build_WithoutConsumerTopologyAndWithoutConfigureTopology_LeavesTopologyNull()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        configurator.ReceiveEndpoint("orders-q", e =>
            e.Consumer<StubConsumer, StubMessage>(c => c.RoutingKey("orders.*")));

        configurator.Build().Topology.Should().BeNull(); // invariant (a) end-to-end (ADR-002)
    }

    [Fact]
    public void DeclareTopology_IsExtensionInTransportAssembly_NotMemberOfAbstractionsSeam()
    {
        // I-2: the helper is NOT a member of the zero-dependency seam interface in Abstractions...
        typeof(IConsumerConfigurator<,>).GetMethod("DeclareTopology").Should().BeNull();

        // ...and lives in the transport assembly (AMQP vocabulary stays on the transport project).
        typeof(ConsumerConfiguratorTopologyExtensions).Assembly.GetName().Name
            .Should().Be("BareWire.Transport.RabbitMQ");
    }

    [Fact]
    public void DeclareTopology_WhenConfiguratorNull_ThrowsArgumentNullException()
    {
        IConsumerConfigurator<StubConsumer, StubMessage> nullConfigurator = null!;

        Action act = () => nullConfigurator.DeclareTopology("x", "q", "k");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DeclareTopology_WhenExchangeEmpty_ThrowsArgumentException()
    {
        ConsumerConfigurator<StubConsumer, StubMessage> cfg = new();

        Action act = () => cfg.DeclareTopology("", "q", "k");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DeclareTopology_WhenBindingKeyNull_ThrowsArgumentNullException()
    {
        ConsumerConfigurator<StubConsumer, StubMessage> cfg = new();

        Action act = () => cfg.DeclareTopology("x", "q", bindingKey: null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Stub types ──────────────────────────────────────────────────────────────

    private sealed record StubMessage;

    private sealed class StubConsumer : IConsumer<StubMessage>
    {
        public Task ConsumeAsync(ConsumeContext<StubMessage> context) => Task.CompletedTask;
    }
}
