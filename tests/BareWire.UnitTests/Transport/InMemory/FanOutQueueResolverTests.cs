using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using BareWire.Transport.InMemory.Topology;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class FanOutQueueResolverTests
{
    private static ExchangeRegistry Registry(Action<ITopologyConfigurator> configure)
    {
        var c = new InMemoryConfigurator();
        c.ConfigureTopology(configure);
        InMemoryTransportOptions o = c.Build();
        return InMemoryTopologyInterpreter.BuildRegistry(o);
    }

    [Fact]
    public void FindConsumerQueues_QueueBoundToFanoutAndTopic_ReturnsBothSortedOrdinal()
    {
        ExchangeRegistry registry = Registry(t =>
        {
            t.DeclareExchange("events", ExchangeType.Fanout);
            t.DeclareExchange("audit", ExchangeType.Topic);
            t.DeclareExchange("commands", ExchangeType.Direct);
            t.DeclareQueue("a-queue");
            t.DeclareQueue("b-queue");
            t.DeclareQueue("c-queue");
            t.BindExchangeToQueue("events", "b-queue", "");
            t.BindExchangeToQueue("audit", "a-queue", "#");
            t.BindExchangeToQueue("commands", "c-queue", "commands.created");
        });

        IReadOnlyList<string> result = FanOutQueueResolver.FindConsumerQueues(
            registry, ["a-queue", "b-queue", "c-queue"]);

        result.Should().Equal("a-queue", "b-queue");
    }

    [Fact]
    public void FindConsumerQueues_DirectExchangeDownstreamOfFanout_IncludesItsQueue()
    {
        ExchangeRegistry registry = Registry(t =>
        {
            t.DeclareExchange("events", ExchangeType.Fanout);
            t.DeclareExchange("routed", ExchangeType.Direct);
            t.DeclareQueue("d-queue");
            t.BindExchangeToExchange("events", "routed", "");
            t.BindExchangeToQueue("routed", "d-queue", "key");
        });

        FanOutQueueResolver.FindConsumerQueues(registry, ["d-queue"]).Should().Equal("d-queue");
    }

    [Fact]
    public void FindConsumerQueues_OnlyDirectBinding_ReturnsEmpty()
    {
        ExchangeRegistry registry = Registry(t =>
        {
            t.DeclareExchange("commands", ExchangeType.Direct);
            t.DeclareQueue("c-queue");
            t.BindExchangeToQueue("commands", "c-queue", "key");
        });

        FanOutQueueResolver.FindConsumerQueues(registry, ["c-queue"]).Should().BeEmpty();
    }

    [Fact]
    public void FindConsumerQueues_FanoutBoundQueueWithoutConsumer_IsExcluded()
    {
        ExchangeRegistry registry = Registry(t =>
        {
            t.DeclareExchange("events", ExchangeType.Fanout);
            t.DeclareQueue("idle");
            t.BindExchangeToQueue("events", "idle", "");
        });

        FanOutQueueResolver.FindConsumerQueues(registry, []).Should().BeEmpty();
    }

    [Fact]
    public void FindConsumerQueues_QueueBoundTwiceToFanOutExchanges_ReturnedOnce()
    {
        ExchangeRegistry registry = Registry(t =>
        {
            t.DeclareExchange("events1", ExchangeType.Fanout);
            t.DeclareExchange("events2", ExchangeType.Fanout);
            t.DeclareQueue("shared");
            t.BindExchangeToQueue("events1", "shared", "");
            t.BindExchangeToQueue("events2", "shared", "");
        });

        FanOutQueueResolver.FindConsumerQueues(registry, ["shared"]).Should().Equal("shared");
    }

    [Fact]
    public void FindConsumerQueues_ExchangeBindingCycle_Terminates()
    {
        ExchangeRegistry registry = Registry(t =>
        {
            t.DeclareExchange("a", ExchangeType.Fanout);
            t.DeclareExchange("b", ExchangeType.Direct);
            t.DeclareQueue("q");
            t.BindExchangeToExchange("a", "b", "");
            t.BindExchangeToExchange("b", "a", "");
            t.BindExchangeToQueue("b", "q", "key");
        });

        FanOutQueueResolver.FindConsumerQueues(registry, ["q"]).Should().Equal("q");
    }
}
