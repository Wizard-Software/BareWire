using System.Buffers;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryTransportAdapterDeferTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static InMemoryTransportAdapter Adapter(
        Action<IInMemoryConfigurator> configure,
        CountingBufferPoolObserver? observer = null,
        TimeProvider? timeProvider = null)
    {
        var c = new InMemoryConfigurator();
        configure(c);
        InMemoryTransportOptions o = c.Build();
        return new InMemoryTransportAdapter(
            o, new InMemoryBroker(o), timeProvider: timeProvider, bufferPoolObserver: observer);
    }

    private static InMemoryTransportAdapter OrdersOnlyAdapter(
        CountingBufferPoolObserver? observer = null, TimeProvider? timeProvider = null) =>
        Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")), observer, timeProvider);

    private static InMemoryTransportAdapter DeferEnabledAdapter(
        TimeSpan delay, CountingBufferPoolObserver? observer = null, TimeProvider? timeProvider = null) =>
        Adapter(
            c =>
            {
                c.EnableDefer(delay);
                c.ConfigureTopology(t => t.DeclareQueue("orders"));
            },
            observer,
            timeProvider);

    private static InMemoryQueue Queue(InMemoryTransportAdapter adapter, string name)
    {
        adapter.Broker.TryGetQueue(name, out InMemoryQueue? queue).Should().BeTrue();
        return queue!;
    }

    private static void Enqueue(InMemoryQueue queue, string id)
    {
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
        int length = Encoding.UTF8.GetBytes(id, buffer);
        InMemoryHeaderSet headers = InMemoryHeaderSet.Stamp(
            new Dictionary<string, string> { [InMemoryHeaderNames.MessageId] = id },
            string.Empty,
            queue.Name,
            string.Empty);
        queue.WriteReserved(new InMemoryDelivery(buffer, length, headers));
    }

    private static async Task<(InboundMessage Message, IAsyncEnumerator<InboundMessage> Enumerator)> ReceiveOneAsync(
        InMemoryTransportAdapter adapter, string queueName, CancellationToken ct = default)
    {
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(queueName, new FlowControlOptions(), ct).GetAsyncEnumerator(ct);
        (await enumerator.MoveNextAsync().AsTask()
            .WaitAsync(WaitTimeout, TestContext.Current.CancellationToken)).Should().BeTrue();
        return (enumerator.Current, enumerator);
    }

    [Fact]
    public async Task SettleAsync_DeferWithoutEnableDefer_ThrowsNotSupportedAndKeepsDeliveryInFlight()
    {
        using InMemoryTransportAdapter adapter = OrdersOnlyAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m1");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        Func<Task> act = async () => await adapter.SettleAsync(SettlementAction.Defer, message);

        await act.Should().ThrowAsync<NotSupportedException>();
        adapter.InFlight.Count.Should().Be(1); // never claimed

        await adapter.SettleAsync(SettlementAction.Ack, message); // still settleable afterwards
        queue.Occupancy.Should().Be(0);
        message.Dispose();
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_DeferWithEnableDefer_RedeliversAfterDelayWithSameSlot()
    {
        var time = new FakeTimeProvider();
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter =
            DeferEnabledAdapter(TimeSpan.FromSeconds(5), observer, time);
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m1");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        await adapter.SettleAsync(SettlementAction.Defer, message);
        message.Dispose();

        adapter.PendingDeferCount.Should().Be(1);
        queue.Occupancy.Should().Be(1); // same reserved slot, held for the deferral

        time.Advance(TimeSpan.FromSeconds(5));

        adapter.PendingDeferCount.Should().Be(0);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        InboundMessage redelivered = enumerator.Current;
        redelivered.MessageId.Should().Be("m1");
        redelivered.Headers[InMemoryHeaderNames.RedeliveryCount].Should().Be("1");

        await adapter.SettleAsync(SettlementAction.Ack, redelivered);
        redelivered.Dispose();
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_WithPendingDefer_ReleasesSlotAndReturnsCopy()
    {
        var time = new FakeTimeProvider();
        var observer = new CountingBufferPoolObserver();
        InMemoryTransportAdapter adapter = DeferEnabledAdapter(TimeSpan.FromSeconds(30), observer, time);
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m1");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        await adapter.SettleAsync(SettlementAction.Defer, message);
        message.Dispose();
        adapter.PendingDeferCount.Should().Be(1);

        adapter.Dispose();

        adapter.PendingDeferCount.Should().Be(0);
        queue.Occupancy.Should().Be(0);
        observer.Outstanding.Should().Be(0);
        observer.Violations.Should().BeEmpty();
        await enumerator.DisposeAsync();
    }

    [Theory]
    [InlineData(TransportAffinity.None)]
    [InlineData(TransportAffinity.SingleActiveConsumer)]
    public void Constructor_EnableDeferWithOrderedEndpoint_ThrowsConfigurationException(TransportAffinity affinity)
    {
        var c = new InMemoryConfigurator();
        c.EnableDefer();
        c.ConfigureTopology(t => t.DeclareQueue("orders"));
        c.ReceiveEndpoint("orders", e => e.OrderedBy(o => o.By<object>(m => m).TransportAffinity(affinity)));

        Action act = () => c.Build();

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be("EnableDefer");
    }

    [Fact]
    public void Constructor_EnableDeferWithoutOrdering_Succeeds()
    {
        var c = new InMemoryConfigurator();
        c.EnableDefer();
        c.ConfigureTopology(t => t.DeclareQueue("orders"));
        c.ReceiveEndpoint("orders", _ => { });

        Action act = () => c.Build();

        act.Should().NotThrow();
    }
}
