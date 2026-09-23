using AwesomeAssertions;
using BareWire.Abstractions.Configuration;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryBrokerTests
{
    private static InMemoryTransportOptions Options(Action<IInMemoryConfigurator> configure)
    {
        var c = new InMemoryConfigurator();
        configure(c);
        return c.Build();
    }

    [Fact]
    public void AdapterConstruction_CreatesOneQueuePerRegistryQueueWithConfiguredCapacity()
    {
        InMemoryTransportOptions o = Options(c =>
        {
            c.QueueCapacity(7);
            c.ConfigureTopology(t =>
            {
                t.DeclareQueue("orders");
                t.DeclareQueue("payments");
            });
        });
        var broker = new InMemoryBroker(o);
        var adapter = new InMemoryTransportAdapter(o, broker);

        broker.QueueCount.Should().Be(adapter.Registry.Queues.Count);
        broker.TryGetQueue("orders", out InMemoryQueue? q).Should().BeTrue();
        q!.Capacity.Should().Be(7);
        broker.ContainsQueue("ghost").Should().BeFalse();
    }

    [Fact]
    public void AttachRegistry_SameRegistryTwice_IsNoOp()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        var broker = new InMemoryBroker(o);
        var adapter = new InMemoryTransportAdapter(o, broker);

        broker.TryGetQueue("orders", out InMemoryQueue? before);
        broker.AttachRegistry(adapter.Registry);
        broker.TryGetQueue("orders", out InMemoryQueue? after);

        after.Should().BeSameAs(before);
    }

    [Fact]
    public void AttachRegistry_DifferentRegistry_Throws()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        var broker = new InMemoryBroker(o);
        _ = new InMemoryTransportAdapter(o, broker);

        Action act = () => _ = new InMemoryTransportAdapter(o, broker); // second adapter builds a new registry instance

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryGetQueue_BeforeAttach_ReturnsFalse()
    {
        InMemoryTransportOptions o = Options(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        var broker = new InMemoryBroker(o);

        broker.TryGetQueue("orders", out _).Should().BeFalse();
        broker.QueueCount.Should().Be(0);
    }
}
