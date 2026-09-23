using System.Buffers;
using System.Diagnostics.Metrics;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryTransportAdapterConsumeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static InMemoryTransportAdapter Adapter(Action<IInMemoryConfigurator> configure, Meter? meter = null)
    {
        var c = new InMemoryConfigurator();
        configure(c);
        InMemoryTransportOptions o = c.Build();
        return new InMemoryTransportAdapter(o, new InMemoryBroker(o), meter: meter);
    }

    private static InMemoryTransportAdapter OrdersAdapter(Meter? meter = null) =>
        Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")), meter);

    private static InMemoryTransportAdapter AffinityAdapter(TransportAffinity affinity) =>
        Adapter(c =>
        {
            c.ConfigureTopology(t => t.DeclareQueue("orders"));
            c.ReceiveEndpoint("orders", e => e.OrderedBy(o => o.TransportAffinity(affinity)));
        });

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

    private static IAsyncEnumerator<InboundMessage> Consume(InMemoryTransportAdapter adapter, CancellationToken ct) =>
        adapter.ConsumeAsync("orders", new FlowControlOptions(), ct).GetAsyncEnumerator(ct);

    private static async Task<bool> NextAsync(IAsyncEnumerator<InboundMessage> enumerator) =>
        await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

    private static string BodyOf(InboundMessage message) => Encoding.UTF8.GetString(message.Body.ToArray());

    // Simulates the consume loop leaving a message unsettled: the token is cancelled (for example while the
    // loop waits for credit) and the enumerator is disposed, which runs the runner's finally block.
    private static async Task AbandonAsync(IAsyncEnumerator<InboundMessage> enumerator, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    // ── ConsumeAsync: delivery map ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_WithQueuedDelivery_YieldsMessageAndTracksItInFlight()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, cts.Token);
        (await NextAsync(consumer)).Should().BeTrue();

        InboundMessage message = consumer.Current;
        message.MessageId.Should().Be("m-1");
        BodyOf(message).Should().Be("m-1");
        message.DeliveryTag.Should().BeGreaterThan(0UL);
        message.PooledBuffer.Should().NotBeNull();
        adapter.InFlight.Count.Should().Be(1);
        queue.HasActiveConsumer.Should().BeTrue();

        adapter.TryTakeInFlight(message, out InFlightDelivery entry).Should().BeTrue();
        entry.Queue.Should().BeSameAs(queue);
        entry.Delivery.MessageId.Should().Be("m-1");
        adapter.InFlight.Count.Should().Be(0);
        adapter.TryTakeInFlight(message, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ConsumeAsync_TwoDeliveries_AssignsDistinctIncreasingTags()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        Enqueue(queue, "m-2");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, cts.Token);
        (await NextAsync(consumer)).Should().BeTrue();
        ulong first = consumer.Current.DeliveryTag;
        (await NextAsync(consumer)).Should().BeTrue();

        consumer.Current.DeliveryTag.Should().BeGreaterThan(first);
        consumer.Current.MessageId.Should().Be("m-2");
        adapter.InFlight.Count.Should().Be(2);
    }

    [Fact]
    public void ConsumeAsync_NullFlowControl_ThrowsArgumentNullException()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();

        Action act = () => adapter.ConsumeAsync("orders", null!, TestContext.Current.CancellationToken);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ConsumeAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();
        await adapter.DisposeAsync();

        Action act = () => adapter.ConsumeAsync("orders", new FlowControlOptions(), TestContext.Current.CancellationToken);

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void TryTakeInFlight_NullMessage_ThrowsArgumentNullException()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();

        Action act = () => adapter.TryTakeInFlight(null!, out _);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── ConsumeAsync: unsettled deliveries go back to the head of the queue ──────────────────────

    [Fact]
    public async Task ConsumeAsync_CancelledWhileWaitingForCredit_ReturnsDeliveryToHeadOfQueue()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        Enqueue(queue, "m-2");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, cts.Token);
        (await NextAsync(consumer)).Should().BeTrue();
        await AbandonAsync(consumer, cts);

        adapter.InFlight.Count.Should().Be(0);
        queue.Occupancy.Should().Be(2);
        queue.HasActiveConsumer.Should().BeFalse();

        await using IAsyncEnumerator<InboundMessage> next = Consume(adapter, TestContext.Current.CancellationToken);
        (await NextAsync(next)).Should().BeTrue();
        next.Current.MessageId.Should().Be("m-1");
        next.Current.Headers[InMemoryHeaderNames.RedeliveryCount].Should().Be("1");
        (await NextAsync(next)).Should().BeTrue();
        next.Current.MessageId.Should().Be("m-2");
    }

    [Fact]
    public async Task ConsumeAsync_CancelledWithUnsettledDelivery_RequeuesCopyAndLeavesOriginalIntact()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, cts.Token);
        (await NextAsync(consumer)).Should().BeTrue();
        InboundMessage original = consumer.Current;
        byte[] originalBuffer = original.PooledBuffer!;
        await AbandonAsync(consumer, cts);

        // The release took the buffer away from the message, so a consumer disposing it late cannot hand
        // the buffer back to the pool while the redelivery is being copied or read.
        original.PooledBuffer.Should().BeNull();

        await using IAsyncEnumerator<InboundMessage> next = Consume(adapter, TestContext.Current.CancellationToken);
        (await NextAsync(next)).Should().BeTrue();
        InboundMessage redelivered = next.Current;

        ReferenceEquals(redelivered.PooledBuffer, originalBuffer).Should().BeFalse();
        BodyOf(redelivered).Should().Be("m-1");
        redelivered.Dispose();
        BodyOf(original).Should().Be("m-1");
        original.Dispose();
    }

    [Fact]
    public async Task ConsumeAsync_CancelledWithTwoUnsettledDeliveries_RequeuesBothAheadInOrder()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        Enqueue(queue, "m-2");
        Enqueue(queue, "m-3");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, cts.Token);
        (await NextAsync(consumer)).Should().BeTrue();
        (await NextAsync(consumer)).Should().BeTrue();
        await AbandonAsync(consumer, cts);

        queue.Occupancy.Should().Be(3);
        await using IAsyncEnumerator<InboundMessage> next = Consume(adapter, TestContext.Current.CancellationToken);
        var order = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            (await NextAsync(next)).Should().BeTrue();
            order.Add(next.Current.MessageId);
        }

        order.Should().Equal("m-1", "m-2", "m-3");
    }

    [Fact]
    public async Task ConsumeAsync_SettledDeliveryBeforeCancellation_IsNotRequeued()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        Enqueue(queue, "m-2");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, cts.Token);
        (await NextAsync(consumer)).Should().BeTrue();
        InboundMessage settled = consumer.Current;
        adapter.TryTakeInFlight(settled, out _).Should().BeTrue();
        queue.ReleaseSlot();
        settled.Dispose();
        await AbandonAsync(consumer, cts);

        queue.Occupancy.Should().Be(1);
        await using IAsyncEnumerator<InboundMessage> next = Consume(adapter, TestContext.Current.CancellationToken);
        (await NextAsync(next)).Should().BeTrue();
        next.Current.MessageId.Should().Be("m-2");
    }

    [Fact]
    public async Task ConsumeAsync_MessageDisposedWithoutSettlement_IsDroppedInsteadOfRequeued()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        Enqueue(queue, "m-2");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, cts.Token);
        (await NextAsync(consumer)).Should().BeTrue();
        consumer.Current.Dispose();                                   // buffer back in the pool, never settled
        await AbandonAsync(consumer, cts);

        adapter.InFlight.Count.Should().Be(0);
        adapter.DisposedUnsettledCount.Should().Be(1);
        queue.Occupancy.Should().Be(1);
        await using IAsyncEnumerator<InboundMessage> next = Consume(adapter, TestContext.Current.CancellationToken);
        (await NextAsync(next)).Should().BeTrue();
        next.Current.MessageId.Should().Be("m-2");
    }

    [Fact]
    public async Task ConsumeAsync_UnsettledMessageDisposedAfterRelease_KeepsRedeliveryAndOriginalReadable()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, cts.Token);
        (await NextAsync(consumer)).Should().BeTrue();
        InboundMessage original = consumer.Current;
        await AbandonAsync(consumer, cts);
        original.Dispose();                                           // a lane finishing after the release

        await using IAsyncEnumerator<InboundMessage> next = Consume(adapter, TestContext.Current.CancellationToken);
        (await NextAsync(next)).Should().BeTrue();
        BodyOf(next.Current).Should().Be("m-1");
        BodyOf(original).Should().Be("m-1");
        adapter.DisposedUnsettledCount.Should().Be(0);
        next.Current.Dispose();
    }

    // ── DisposeAsync: sweep the delivery map ──────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_WithUnsettledDelivery_EmptiesMapReleasesSlotAndCountsDrop()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, TestContext.Current.CancellationToken);
        (await NextAsync(consumer)).Should().BeTrue();
        InboundMessage message = consumer.Current;

        await adapter.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        adapter.InFlight.Count.Should().Be(0);
        queue.Occupancy.Should().Be(0);
        adapter.DroppedOnShutdownCount.Should().Be(1);
        BodyOf(message).Should().Be("m-1");
        message.PooledBuffer.Should().NotBeNull();

        message.Dispose();
        await consumer.DisposeAsync();

        queue.Occupancy.Should().Be(0);
        adapter.DroppedOnShutdownCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_WithMeter_RecordsDroppedDeliveriesOnCounter()
    {
        using var meter = new Meter("BareWire.UnitTests.InMemoryConsume." + Guid.NewGuid().ToString("N"));
        long recorded = 0;
        string? recordedQueue = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name == InMemoryConsumeDiagnostics.DroppedOnShutdownCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            recorded += value;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "queue")
                {
                    recordedQueue = tag.Value as string;
                }
            }
        });
        listener.Start();

        InMemoryTransportAdapter adapter = OrdersAdapter(meter);
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        await using IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, TestContext.Current.CancellationToken);
        (await NextAsync(consumer)).Should().BeTrue();

        await adapter.DisposeAsync();

        recorded.Should().Be(1);
        recordedQueue.Should().Be("orders");
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        await using IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, TestContext.Current.CancellationToken);
        (await NextAsync(consumer)).Should().BeTrue();

        await adapter.DisposeAsync();
        Func<Task> again = async () => await adapter.DisposeAsync();

        await again.Should().NotThrowAsync();
        adapter.DroppedOnShutdownCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_WhileConsumerWaitsForDeliveries_CompletesTheEnumerator()
    {
        InMemoryTransportAdapter adapter = OrdersAdapter();
        IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, TestContext.Current.CancellationToken);
        Task<bool> pending = consumer.MoveNextAsync().AsTask();

        await adapter.DisposeAsync();

        Func<Task> wait = async () =>
        {
            try
            {
                (await pending.WaitAsync(Timeout, TestContext.Current.CancellationToken)).Should().BeFalse();
            }
            catch (OperationCanceledException)
            {
                // the shutdown token surfaces as a cancellation of the pending read — an accepted outcome
            }
        };

        await wait.Should().NotThrowAsync();
        await consumer.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WithSingleActiveConsumerAndStandbyRunner_CompletesBothEnumerators()
    {
        InMemoryTransportAdapter adapter = AffinityAdapter(TransportAffinity.SingleActiveConsumer);
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        IAsyncEnumerator<InboundMessage> active = Consume(adapter, TestContext.Current.CancellationToken);
        (await NextAsync(active)).Should().BeTrue();
        IAsyncEnumerator<InboundMessage> standby = Consume(adapter, TestContext.Current.CancellationToken);
        Task<bool> standbyMove = standby.MoveNextAsync().AsTask();

        await adapter.DisposeAsync();

        Func<Task> finish = async () =>
        {
            await CompleteQuietlyAsync(standbyMove);
            await active.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
            await standby.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        };

        await finish.Should().NotThrowAsync();
        queue.Occupancy.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_ViaServiceProviderSynchronousDispose_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddBareWireInMemory(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        ServiceProvider provider = services.BuildServiceProvider();
        var adapter = (InMemoryTransportAdapter)provider.GetRequiredService<ITransportAdapter>();
        Enqueue(Queue(adapter, "orders"), "m-1");
        await using IAsyncEnumerator<InboundMessage> consumer = Consume(adapter, TestContext.Current.CancellationToken);
        (await NextAsync(consumer)).Should().BeTrue();

        Action act = provider.Dispose;

        act.Should().NotThrow();
        adapter.DroppedOnShutdownCount.Should().Be(1);
    }

    private static async Task CompleteQuietlyAsync(Task<bool> move)
    {
        try
        {
            await move.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // cancellation by the shutdown token is an accepted way for a pending read to end
        }
    }

    // ── SingleActiveConsumer ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_SingleActiveConsumer_SecondRunnerStaysStandbyUntilFirstEnds()
    {
        InMemoryTransportAdapter adapter = AffinityAdapter(TransportAffinity.SingleActiveConsumer);
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        Enqueue(queue, "m-2");
        using var first = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        IAsyncEnumerator<InboundMessage> active = Consume(adapter, first.Token);
        (await NextAsync(active)).Should().BeTrue();

        await using IAsyncEnumerator<InboundMessage> standby = Consume(adapter, TestContext.Current.CancellationToken);
        Task<bool> standbyMove = standby.MoveNextAsync().AsTask();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        standbyMove.IsCompleted.Should().BeFalse();
        queue.ActiveConsumerCount.Should().Be(1);

        await AbandonAsync(active, first);

        (await standbyMove.WaitAsync(Timeout, TestContext.Current.CancellationToken)).Should().BeTrue();
        standby.Current.MessageId.Should().Be("m-1");
        queue.ActiveConsumerCount.Should().Be(1);
    }

    [Fact]
    public async Task ConsumeAsync_WithoutSingleActiveConsumer_AllowsTwoActiveRunners()
    {
        InMemoryTransportAdapter adapter = AffinityAdapter(TransportAffinity.None);
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m-1");
        Enqueue(queue, "m-2");

        await using IAsyncEnumerator<InboundMessage> a = Consume(adapter, TestContext.Current.CancellationToken);
        await using IAsyncEnumerator<InboundMessage> b = Consume(adapter, TestContext.Current.CancellationToken);
        (await NextAsync(a)).Should().BeTrue();
        (await NextAsync(b)).Should().BeTrue();

        new[] { a.Current.MessageId, b.Current.MessageId }.Should().BeEquivalentTo("m-1", "m-2");
        queue.ActiveConsumerCount.Should().Be(2);
    }

    // ── ConsistentHash ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_WithConsistentHashAffinity_ThrowsConfigurationException()
    {
        Action act = () => AffinityAdapter(TransportAffinity.ConsistentHash);

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*ConsistentHash*orders*");
    }
}
