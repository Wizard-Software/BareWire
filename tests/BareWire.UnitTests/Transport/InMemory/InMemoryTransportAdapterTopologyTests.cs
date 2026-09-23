using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryTransportAdapterTopologyTests
{
    private static InMemoryTransportOptions Options(Action<IInMemoryConfigurator> configure)
    {
        var c = new InMemoryConfigurator();
        configure(c);
        return c.Build();
    }

    private static InMemoryTransportAdapter Adapter(Action<IInMemoryConfigurator> configure)
    {
        InMemoryTransportOptions o = Options(configure);
        return new InMemoryTransportAdapter(o, new InMemoryBroker(o));
    }

    [Fact]
    public void Constructor_WithInvalidTopologyConfiguration_ThrowsConfigurationException()
    {
        Action act = () => Adapter(c => c.ReceiveEndpoint("orders", _ => { }));

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*orders*");
    }

    [Fact]
    public void Constructor_WithValidConfiguration_ExposesSealedRegistry()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));

        adapter.Registry.Should().NotBeNull();
        adapter.Registry.ContainsQueue("orders").Should().BeTrue();
    }

    [Fact]
    public void GetRequiredService_WithUndeclaredDefaultExchange_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        services.AddBareWireInMemory(t => t.DefaultExchange("missing"));

        using ServiceProvider sp = services.BuildServiceProvider();

        Action act = () => sp.GetRequiredService<ITransportAdapter>();

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*missing*");
    }

    [Fact]
    public async Task DeployTopologyAsync_SameInstance_IsNoOp()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        var adapter = new InMemoryTransportAdapter(o, new InMemoryBroker(o));

        Func<Task> act = () => adapter.DeployTopologyAsync(o.Topology!, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeployTopologyAsync_IdenticalDeclarationInDifferentOrder_IsNoOp()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("ex1", ExchangeType.Direct);
            t.DeclareExchange("ex2", ExchangeType.Fanout);
        }));
        var reordered = new TopologyDeclaration
        {
            Exchanges =
            [
                new ExchangeDeclaration("ex2", ExchangeType.Fanout),
                new ExchangeDeclaration("ex1", ExchangeType.Direct),
            ],
        };

        Func<Task> act = () => adapter.DeployTopologyAsync(reordered, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeployTopologyAsync_EmptyDeclarationWhenNoTopologyConfigured_IsNoOp()
    {
        InMemoryTransportAdapter adapter = Adapter(_ => { });

        Func<Task> act = () => adapter.DeployTopologyAsync(
            new TopologyDeclaration(), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeployTopologyAsync_AddedQueue_ThrowsConfigurationException()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        var changed = new TopologyDeclaration
        {
            Queues = [new QueueDeclaration("orders"), new QueueDeclaration("extra")],
        };

        Func<Task> act = () => adapter.DeployTopologyAsync(changed, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BareWireConfigurationException>();
        adapter.Registry.ContainsQueue("extra").Should().BeFalse();
    }

    [Fact]
    public async Task DeployTopologyAsync_ChangedExchangeType_ThrowsConfigurationException()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(
            t => t.DeclareExchange("ex", ExchangeType.Direct)));
        var changed = new TopologyDeclaration { Exchanges = [new ExchangeDeclaration("ex", ExchangeType.Fanout)] };

        Func<Task> act = () => adapter.DeployTopologyAsync(changed, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BareWireConfigurationException>();
    }

    [Fact]
    public async Task DeployTopologyAsync_AddedBinding_ThrowsConfigurationException()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("ex", ExchangeType.Direct);
            t.DeclareQueue("q");
        }));
        var changed = new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration("ex", ExchangeType.Direct)],
            Queues = [new QueueDeclaration("q")],
            ExchangeQueueBindings = [new ExchangeQueueBinding("ex", "q", "k")],
        };

        Func<Task> act = () => adapter.DeployTopologyAsync(changed, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BareWireConfigurationException>();
    }

    [Fact]
    public async Task DeployTopologyAsync_QueueWithTtlArgument_ThrowsConfigurationException()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        var changed = new TopologyDeclaration
        {
            Queues =
            [
                new QueueDeclaration(
                    "orders",
                    Arguments: new Dictionary<string, object> { ["x-message-ttl"] = 60_000L }),
            ],
        };

        // The frozen-topology check wins over the unsupported-argument check: a redeployment that both
        // changes the declaration AND introduces an unsupported argument is rejected as "not identical
        // to the sealed topology", not as "unsupported argument".
        Func<Task> act = () => adapter.DeployTopologyAsync(changed, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BareWireConfigurationException>();
    }

    [Fact]
    public async Task DeployTopologyAsync_HeadersExchange_ThrowsTransportException()
    {
        InMemoryTransportAdapter adapter = Adapter(_ => { });
        var withHeaders = new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration("h", ExchangeType.Headers)],
        };

        Func<Task> act = () => adapter.DeployTopologyAsync(withHeaders, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BareWireTransportException>().WithMessage("*Headers*");
    }

    [Fact]
    public async Task DeployTopologyAsync_Null_ThrowsArgumentNullException()
    {
        InMemoryTransportAdapter adapter = Adapter(_ => { });

        Func<Task> act = () => adapter.DeployTopologyAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DeployTopologyAsync_AfterRejectedDeclaration_RegistryUnchanged()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        var changed = new TopologyDeclaration
        {
            Queues = [new QueueDeclaration("orders"), new QueueDeclaration("extra")],
        };

        Func<Task> act = () => adapter.DeployTopologyAsync(changed, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<BareWireConfigurationException>();

        adapter.Registry.Queues.Should().ContainSingle();
        adapter.Registry.ContainsQueue("orders").Should().BeTrue();
    }

    [Fact]
    public void ConsumeAsync_UndeclaredQueue_ThrowsConfigurationException()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));

        Action act = () => adapter.ConsumeAsync("ghost", new FlowControlOptions(), TestContext.Current.CancellationToken);

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*ghost*");
        adapter.Registry.ContainsQueue("ghost").Should().BeFalse();
    }

    [Fact]
    public void ConsumeAsync_UndeclaredQueue_DoesNotCreateQueue()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        int queuesBefore = adapter.Registry.Queues.Count;

        Action act = () => adapter.ConsumeAsync("ghost", new FlowControlOptions(), TestContext.Current.CancellationToken);

        act.Should().Throw<BareWireConfigurationException>();
        adapter.Registry.ContainsQueue("ghost").Should().BeFalse();
        adapter.Registry.Queues.Count.Should().Be(queuesBefore);
        adapter.Broker.ContainsQueue("ghost").Should().BeFalse();
        adapter.Broker.QueueCount.Should().Be(queuesBefore);
    }

    [Fact]
    public void ConsumeAsync_DeclaredQueue_StillThrowsNotSupported()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));

        Action act = () => adapter.ConsumeAsync("orders", new FlowControlOptions(), TestContext.Current.CancellationToken);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void ManualTopology_WithoutAutoDeclare_CreatesNothingAutomatically()
    {
        Action noQueueDeclared = () => Adapter(c => c.ReceiveEndpoint("orders", _ => { }));
        noQueueDeclared.Should().Throw<BareWireConfigurationException>();

        InMemoryTransportAdapter emptyAdapter = Adapter(_ => { });
        emptyAdapter.Registry.Exchanges.Should().BeEmpty();
        emptyAdapter.Registry.Queues.Should().BeEmpty();
        emptyAdapter.Registry.ExchangeQueueBindings.Should().BeEmpty();
        emptyAdapter.Registry.ExchangeExchangeBindings.Should().BeEmpty();
        emptyAdapter.Registry.AutoDeclaredQueueNames.Should().BeEmpty();

        InMemoryTransportAdapter manualAdapter = Adapter(c =>
        {
            c.ConfigureTopology(t => t.DeclareQueue("orders"));
            c.ReceiveEndpoint("orders", _ => { });
        });
        manualAdapter.Registry.Queues.Keys.Should().BeEquivalentTo(["orders"]);
        manualAdapter.Registry.AutoDeclaredQueueNames.Should().BeEmpty();
    }
}
