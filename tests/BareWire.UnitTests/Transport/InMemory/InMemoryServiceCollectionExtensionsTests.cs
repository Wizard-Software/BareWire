using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryServiceCollectionExtensionsTests
{
    private static ServiceProvider Build(Action<IInMemoryConfigurator>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireInMemory(configure ?? (_ => { }));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddBareWireInMemory_Registered_ResolvesAdapterWithNoCapabilities()
    {
        using ServiceProvider sp = Build();
        ITransportAdapter adapter = sp.GetRequiredService<ITransportAdapter>();
        adapter.Should().BeOfType<InMemoryTransportAdapter>();
        adapter.Capabilities.Should().Be(TransportCapabilities.None);
        adapter.TransportName.Should().Be("InMemory");
    }

    [Fact]
    public void AddBareWireInMemory_SameContainer_ResolvesSingleBrokerInstance()
    {
        using ServiceProvider sp = Build();
        sp.GetRequiredService<InMemoryBroker>().Should().BeSameAs(sp.GetRequiredService<InMemoryBroker>());
        ((InMemoryTransportAdapter)sp.GetRequiredService<ITransportAdapter>()).Broker
            .Should().BeSameAs(sp.GetRequiredService<InMemoryBroker>());
    }

    [Fact]
    public void AddBareWireInMemory_TwoContainers_DoNotShareQueues()
    {
        using ServiceProvider first = Build(t => t.ConfigureTopology(topo => topo.DeclareQueue("orders")));
        using ServiceProvider second = Build(t => t.ConfigureTopology(topo => topo.DeclareQueue("orders")));
        _ = first.GetRequiredService<ITransportAdapter>();
        _ = second.GetRequiredService<ITransportAdapter>();
        InMemoryBroker a = first.GetRequiredService<InMemoryBroker>();
        InMemoryBroker b = second.GetRequiredService<InMemoryBroker>();
        a.Should().NotBeSameAs(b);

        a.TryGetQueue("orders", out InMemoryQueue? qa).Should().BeTrue();
        b.TryGetQueue("orders", out InMemoryQueue? qb).Should().BeTrue();
        qa.Should().NotBeSameAs(qb);
        qa!.TryReserve().Should().Be(QueueReservationResult.Reserved);
        qb!.Occupancy.Should().Be(0);
    }

    [Fact]
    public void AddBareWireInMemory_WithInvalidOption_ThrowsConfigurationException() =>
        new ServiceCollection().Invoking(s => s.AddBareWireInMemory(t => t.MaxMessageSize(0)))
            .Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be("MaxMessageSize");

    [Fact]
    public void AddBareWireInMemory_WithReceiveEndpoint_RegistersEndpointBinding()
    {
        using ServiceProvider sp = Build(t =>
        {
            t.ConfigureTopology(topo => topo.DeclareQueue("q"));
            t.ReceiveEndpoint("q", e => e.Consumer<TestConsumer, OrderCreated>());
        });

        IReadOnlyList<EndpointBinding> bindings = sp.GetRequiredService<IReadOnlyList<EndpointBinding>>();
        bindings.Should().ContainSingle();
        bindings[0].EndpointName.Should().Be("q");
        bindings[0].Consumers.Should().ContainSingle();

        sp.GetRequiredService<TopologyDeclaration>().Queues.Should().ContainSingle(q => q.Name == "q");
    }

    [Fact]
    public void AddBareWireInMemory_WithDeadLetterQueueArgument_MapsDeadLetterExchangeOnBinding()
    {
        using ServiceProvider sp = Build(t =>
        {
            t.ConfigureTopology(topo =>
                topo.DeclareQueue("q", true, false, q => q.DeadLetterExchange("dlx")));
            t.ReceiveEndpoint("q", e => e.Consumer<TestConsumer, OrderCreated>());
        });

        IReadOnlyList<EndpointBinding> bindings = sp.GetRequiredService<IReadOnlyList<EndpointBinding>>();
        bindings.Should().ContainSingle();
        bindings[0].DeadLetterExchange.Should().Be("dlx");
    }

    [Fact]
    public void AddBareWireInMemory_WithNullServices_ThrowsArgumentNullException()
    {
        IServiceCollection? services = null;
        Action act = () => services!.AddBareWireInMemory(_ => { });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddBareWireInMemory_WithNullConfigure_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        services.Invoking(s => s.AddBareWireInMemory(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddBareWireInMemory_WithMapExchangeAndRoutingKey_ReplacesResolvers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var defaultExchangeResolver = Substitute.For<IExchangeResolver>();
        var defaultRoutingKeyResolver = Substitute.For<IRoutingKeyResolver>();
        services.TryAddSingleton(defaultExchangeResolver);
        services.TryAddSingleton(defaultRoutingKeyResolver);

        services.AddBareWireInMemory(t =>
        {
            t.MapExchange<OrderCreated>("orders");
            t.MapRoutingKey<OrderCreated>("order.created");
        });

        using ServiceProvider sp = services.BuildServiceProvider();
        IExchangeResolver exchangeResolver = sp.GetRequiredService<IExchangeResolver>();
        IRoutingKeyResolver routingKeyResolver = sp.GetRequiredService<IRoutingKeyResolver>();

        exchangeResolver.Should().NotBeSameAs(defaultExchangeResolver);
        routingKeyResolver.Should().NotBeSameAs(defaultRoutingKeyResolver);
        exchangeResolver.Resolve<OrderCreated>().Should().Be("orders");
        routingKeyResolver.Resolve<OrderCreated>().Should().Be("order.created");
    }

    [Fact]
    public void AddBareWireInMemory_CalledTwice_KeepsFirstRoutingMappings()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireInMemory(t => t.MapExchange<OrderCreated>("first"));
        services.AddBareWireInMemory(t => t.MapExchange<OrderCreated>("second"));

        using ServiceProvider sp = services.BuildServiceProvider();

        sp.GetRequiredService<IExchangeResolver>().Resolve<OrderCreated>().Should().Be("first");
        sp.GetServices<InMemoryTransportOptions>().Should().ContainSingle();
    }

    [Fact]
    public void AddBareWireInMemory_SecondCallWithInvalidOption_StillThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        services.AddBareWireInMemory(_ => { });

        services.Invoking(s => s.AddBareWireInMemory(t => t.QueueCapacity(0)))
            .Should().Throw<BareWireConfigurationException>();
    }

    [Fact]
    public async Task SendBatchAsync_OnSkeletonAdapter_ThrowsNotSupportedException()
    {
        using ServiceProvider sp = Build();
        ITransportAdapter adapter = sp.GetRequiredService<ITransportAdapter>();

        Func<Task> act = () => adapter.SendBatchAsync([]);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private sealed record OrderCreated(Guid Id);

    private sealed class TestConsumer : IConsumer<OrderCreated>
    {
        public Task ConsumeAsync(ConsumeContext<OrderCreated> context) => Task.CompletedTask;
    }
}
