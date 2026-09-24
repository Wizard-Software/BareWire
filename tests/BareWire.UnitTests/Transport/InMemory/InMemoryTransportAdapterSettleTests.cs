using System.Buffers;
using System.Diagnostics.Metrics;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryTransportAdapterSettleTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────────────

    private static InMemoryTransportAdapter Adapter(
        Action<IInMemoryConfigurator> configure, CountingBufferPoolObserver? observer = null)
    {
        var c = new InMemoryConfigurator();
        configure(c);
        InMemoryTransportOptions o = c.Build();
        return new InMemoryTransportAdapter(o, new InMemoryBroker(o), bufferPoolObserver: observer);
    }

    private static InMemoryTransportAdapter OrdersOnlyAdapter(
        CountingBufferPoolObserver? observer = null, int? maxRedeliveries = null) =>
        Adapter(c =>
        {
            if (maxRedeliveries is { } mr)
            {
                c.MaxRedeliveries(mr);
            }

            c.ConfigureTopology(t => t.DeclareQueue("orders"));
        }, observer);

    // "orders" declares a DLX ("dlx", fanout) bound to a single "orders-dlq".
    private static InMemoryTransportAdapter DlxAdapter(
        CountingBufferPoolObserver? observer = null, int? maxRedeliveries = null, int? queueCapacity = null) =>
        Adapter(c =>
        {
            if (maxRedeliveries is { } mr)
            {
                c.MaxRedeliveries(mr);
            }

            if (queueCapacity is { } qc)
            {
                c.QueueCapacity(qc);
            }

            c.ConfigureTopology(t =>
            {
                t.DeclareQueue("orders", true, false, q => q.DeadLetterExchange("dlx"));
                t.DeclareExchange("dlx", ExchangeType.Fanout);
                t.DeclareQueue("orders-dlq");
                t.BindExchangeToQueue("dlx", "orders-dlq", string.Empty);
            });
        }, observer);

    // "orders" declares a DLX with no queue bound to it — every route is Unroutable.
    private static InMemoryTransportAdapter DlxUnroutableAdapter(CountingBufferPoolObserver? observer = null) =>
        Adapter(c => c.ConfigureTopology(t =>
        {
            t.DeclareQueue("orders", true, false, q => q.DeadLetterExchange("dlx"));
            t.DeclareExchange("dlx", ExchangeType.Fanout);
        }), observer);

    // Direct DLX: "orders-dlq" is bound under routing key "dead", while the original publish routing key
    // ("orders", stamped by Enqueue below) is "orders" — only the DeadLetterRoutingKey override makes this
    // land.
    private static InMemoryTransportAdapter DirectDlxWithRoutingKeyOverrideAdapter(
        CountingBufferPoolObserver? observer = null) =>
        Adapter(c => c.ConfigureTopology(t =>
        {
            t.DeclareQueue("orders", true, false, q => q.DeadLetterExchange("dlx").DeadLetterRoutingKey("dead"));
            t.DeclareExchange("dlx", ExchangeType.Direct);
            t.DeclareQueue("orders-dlq");
            t.BindExchangeToQueue("dlx", "orders-dlq", "dead");
        }), observer);

    // Fanout DLX bound to two DLQs.
    private static InMemoryTransportAdapter FanOutTwoDlqAdapter(CountingBufferPoolObserver? observer = null) =>
        Adapter(c => c.ConfigureTopology(t =>
        {
            t.DeclareQueue("orders", true, false, q => q.DeadLetterExchange("dlx"));
            t.DeclareExchange("dlx", ExchangeType.Fanout);
            t.DeclareQueue("orders-dlq");
            t.DeclareQueue("orders-dlq-2");
            t.BindExchangeToQueue("dlx", "orders-dlq", string.Empty);
            t.BindExchangeToQueue("dlx", "orders-dlq-2", string.Empty);
        }), observer);

    // A fanout exchange "events" feeding three queues: q1 (no DLX), q2 (DLX -> q2-dlq), q3 (no DLX).
    private static InMemoryTransportAdapter FanOutThreeAdapter(CountingBufferPoolObserver? observer = null) =>
        Adapter(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("events", ExchangeType.Fanout);
            t.DeclareQueue("q1");
            t.DeclareQueue("q2", true, false, q => q.DeadLetterExchange("dlx"));
            t.DeclareQueue("q3");
            t.BindExchangeToQueue("events", "q1", string.Empty);
            t.BindExchangeToQueue("events", "q2", string.Empty);
            t.BindExchangeToQueue("events", "q3", string.Empty);
            t.DeclareExchange("dlx", ExchangeType.Fanout);
            t.DeclareQueue("q2-dlq");
            t.BindExchangeToQueue("dlx", "q2-dlq", string.Empty);
        }), observer);

    private static InMemoryQueue Queue(InMemoryTransportAdapter adapter, string name)
    {
        adapter.Broker.TryGetQueue(name, out InMemoryQueue? queue).Should().BeTrue();
        return queue!;
    }

    // Enqueues directly via TryReserve/WriteReserved with a buffer rented straight from
    // ArrayPool<byte>.Shared — deliberately OUTSIDE the buffer-pool hook, so the observer counts only
    // rents the transport itself performs. The routing key stamped is the queue's own name (mirrors
    // InMemoryTransportAdapterConsumeTests' fixture).
    private static void Enqueue(InMemoryQueue queue, string id, string? routingKey = null)
    {
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16);
        int length = Encoding.UTF8.GetBytes(id, buffer);
        InMemoryHeaderSet headers = InMemoryHeaderSet.Stamp(
            new Dictionary<string, string> { [InMemoryHeaderNames.MessageId] = id },
            string.Empty,
            routingKey ?? queue.Name,
            string.Empty);
        queue.WriteReserved(new InMemoryDelivery(buffer, length, headers));
    }

    private static string BodyOf(InboundMessage message) => Encoding.UTF8.GetString(message.Body.ToArray());

    // M3: returns the enumerator ALONGSIDE the message, and callers must keep it alive until AFTER
    // settling — disposing it first would let the runner's own cleanup requeue/settle the delivery,
    // making the SettleAsync call under test a no-op for the wrong reason.
    private static async Task<(InboundMessage Message, IAsyncEnumerator<InboundMessage> Enumerator)> ReceiveOneAsync(
        InMemoryTransportAdapter adapter, string queueName, CancellationToken ct = default)
    {
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(queueName, new FlowControlOptions(), ct).GetAsyncEnumerator(ct);
        (await enumerator.MoveNextAsync().AsTask()
            .WaitAsync(WaitTimeout, TestContext.Current.CancellationToken)).Should().BeTrue();
        return (enumerator.Current, enumerator);
    }

    private static OutboundMessage ToExchange(string exchange, string routingKey, string id, string body) =>
        new(
            routingKey,
            new Dictionary<string, string>
            {
                [InMemoryHeaderNames.Exchange] = exchange,
                [InMemoryHeaderNames.MessageId] = id,
            },
            Encoding.UTF8.GetBytes(body),
            string.Empty);

    // ── Step 1: buffer pool + observer ────────────────────────────────────────────────────────────

    [Fact]
    public void BufferPool_RentThenReturn_ObserverSeesExactlyOneOfEach()
    {
        var observer = new CountingBufferPoolObserver();
        var pool = new InMemoryBufferPool(observer);

        byte[] buffer = pool.Rent(0);
        buffer.Length.Should().BeGreaterThan(0);
        pool.Return(buffer);

        observer.Rented.Should().Be(1);
        observer.Returned.Should().Be(1);
        observer.Violations.Should().BeEmpty();
    }

    // ── Step 2: shared body copier + runner on the pool ───────────────────────────────────────────

    [Fact]
    public async Task RunnerRequeue_WhenEnumerationEndsUnsettled_CopyIsRentedThroughPool()
    {
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter = OrdersOnlyAdapter(observer);
        Enqueue(Queue(adapter, "orders"), "m1");
        using var cts = new CancellationTokenSource();

        await using (IAsyncEnumerator<InboundMessage> e =
            adapter.ConsumeAsync("orders", new FlowControlOptions(), cts.Token).GetAsyncEnumerator(cts.Token))
        {
            (await e.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken))
                .Should().BeTrue();
        } // enumerator ends unsettled -> copy requeued at head, through the pool

        observer.Rented.Should().Be(1); // the requeue copy only (Enqueue rents outside the hook)
        observer.Violations.Should().BeEmpty();
        Queue(adapter, "orders").Occupancy.Should().Be(1);
    }

    // ── Step 3: settlement-drop diagnostics ───────────────────────────────────────────────────────

    [Fact]
    public void SettlementDropped_WithMeter_IncrementsCounterTaggedWithReason()
    {
        using var meter = new Meter("BareWire.UnitTests.Settlement." + Guid.NewGuid().ToString("N"));
        var recorded = new List<(long Value, string? Queue, string? Reason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name == InMemoryConsumeDiagnostics.SettlementDroppedCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? queue = null;
            string? reason = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "queue")
                {
                    queue = tag.Value as string;
                }
                else if (tag.Key == "reason")
                {
                    reason = tag.Value as string;
                }
            }

            recorded.Add((value, queue, reason));
        });
        listener.Start();

        var diagnostics = new InMemoryConsumeDiagnostics(NullLogger.Instance, meter);
        diagnostics.SettlementDropped("orders", SettlementDropReason.DeadLetterQueueFull);

        diagnostics.SettlementDroppedCount(SettlementDropReason.DeadLetterQueueFull).Should().Be(1);
        recorded.Should().ContainSingle(r => r.Reason == "dlx_full" && r.Queue == "orders" && r.Value == 1);
    }

    [Fact]
    public void SettlementDropped_MultipleDropsWithinOneWindow_LogsOnceButCountsEvery()
    {
        var time = new FakeTimeProvider();
        var logger = new WarningCountingLogger();
        var diagnostics = new InMemoryConsumeDiagnostics(logger, meter: null, timeProvider: time);

        for (int i = 0; i < 5; i++)
        {
            diagnostics.SettlementDropped("orders", SettlementDropReason.NoDeadLetterExchange);
        }

        logger.WarningCount.Should().Be(1);
        diagnostics.SettlementDroppedCount(SettlementDropReason.NoDeadLetterExchange).Should().Be(5);
    }

    [Fact]
    public void SettlementDropped_SecondDropAfterWindowElapses_LogsAgain()
    {
        var time = new FakeTimeProvider();
        var logger = new WarningCountingLogger();
        var diagnostics = new InMemoryConsumeDiagnostics(logger, meter: null, timeProvider: time);

        diagnostics.SettlementDropped("orders", SettlementDropReason.NoDeadLetterExchange);
        time.Advance(TimeSpan.FromSeconds(61));
        diagnostics.SettlementDropped("orders", SettlementDropReason.NoDeadLetterExchange);

        logger.WarningCount.Should().Be(2);
    }

    // ── Step 4: Ack / Requeue / redelivery limit ──────────────────────────────────────────────────

    [Fact]
    public async Task SettleAsync_Ack_ReleasesSlotAndNeverTouchesBuffer()
    {
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter = DlxAdapter(observer);
        Enqueue(Queue(adapter, "orders"), "m1");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        await adapter.SettleAsync(SettlementAction.Ack, message);

        Queue(adapter, "orders").Occupancy.Should().Be(0);
        observer.Rented.Should().Be(0);
        observer.Returned.Should().Be(0);
        message.PooledBuffer.Should().NotBeNull(); // still owned by the message
        message.Dispose();
        message.PooledBuffer.Should().BeNull(); // returned exactly once, by the message
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_Requeue_PutsCopyAtHeadWithSameSlotAndIncrementedCount()
    {
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter = OrdersOnlyAdapter(observer);
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m1");
        Enqueue(queue, "m2");
        (InboundMessage m1, IAsyncEnumerator<InboundMessage> e1) = await ReceiveOneAsync(adapter, "orders");

        await adapter.SettleAsync(SettlementAction.Requeue, m1);

        queue.Occupancy.Should().Be(2); // unchanged: same reserved slot, reused
        observer.Rented.Should().Be(1); // the requeue copy
        observer.Returned.Should().Be(0); // not consumed/disposed yet
        m1.PooledBuffer.Should().NotBeNull(); // the original is untouched
        m1.Dispose();

        (await e1.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        InboundMessage redelivered = e1.Current;
        redelivered.MessageId.Should().Be("m1");
        redelivered.Headers[InMemoryHeaderNames.RedeliveryCount].Should().Be("1");
        BodyOf(redelivered).Should().Be("m1");
        redelivered.Dispose();
        await e1.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_RequeueBeyondMaxRedeliveries_DeadLettersInsteadOfRequeueing()
    {
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter = DlxAdapter(observer, maxRedeliveries: 2);
        InMemoryQueue orders = Queue(adapter, "orders");
        InMemoryQueue dlq = Queue(adapter, "orders-dlq");
        Enqueue(orders, "m1");

        // requeue #1 (count -> 1) and #2 (count -> 2): still under the limit, still requeued.
        (InboundMessage first, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");
        await adapter.SettleAsync(SettlementAction.Requeue, first);
        first.Dispose();
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        InboundMessage second = enumerator.Current;
        second.Headers[InMemoryHeaderNames.RedeliveryCount].Should().Be("1");
        await adapter.SettleAsync(SettlementAction.Requeue, second);
        second.Dispose();
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        InboundMessage third = enumerator.Current;
        third.Headers[InMemoryHeaderNames.RedeliveryCount].Should().Be("2");

        // third Requeue: redelivery count (2) already equals MaxRedeliveries (2) -> dead-letters instead.
        await adapter.SettleAsync(SettlementAction.Requeue, third);
        third.Dispose();

        orders.Occupancy.Should().Be(0);
        await enumerator.DisposeAsync();

        (InboundMessage dead, IAsyncEnumerator<InboundMessage> deadEnumerator) = await ReceiveOneAsync(adapter, "orders-dlq");
        dead.MessageId.Should().Be("m1");
        dead.Headers.ContainsKey(InMemoryHeaderNames.RedeliveryCount).Should().BeFalse(); // reset to 0
        dlq.Occupancy.Should().Be(1);
        await adapter.SettleAsync(SettlementAction.Ack, dead);
        dead.Dispose();
        await deadEnumerator.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_RequeueBeyondMaxRedeliveries_WithoutDlx_DropsWithMaxRedeliveriesReason()
    {
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter = OrdersOnlyAdapter(observer, maxRedeliveries: 1);
        InMemoryQueue orders = Queue(adapter, "orders");
        Enqueue(orders, "m1");

        (InboundMessage first, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");
        await adapter.SettleAsync(SettlementAction.Requeue, first); // count -> 1, still under the limit (1)
        first.Dispose();
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        InboundMessage second = enumerator.Current;
        second.Headers[InMemoryHeaderNames.RedeliveryCount].Should().Be("1");

        await adapter.SettleAsync(SettlementAction.Requeue, second); // count (1) >= limit (1) -> drop, no DLX
        second.Dispose();

        orders.Occupancy.Should().Be(0);
        observer.Rented.Should().Be(1); // only the first (accepted) requeue copy
        adapter.SettlementDroppedCount(SettlementDropReason.MaxRedeliveries).Should().Be(1);
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_UnknownOrAlreadySettledMessage_IsNoOp()
    {
        using InMemoryTransportAdapter adapter = DlxAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m1");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");
        await adapter.SettleAsync(SettlementAction.Ack, message);
        int occupancyAfterFirstAck = queue.Occupancy;

        Func<Task> secondAck = async () => await adapter.SettleAsync(SettlementAction.Ack, message);

        await secondAck.Should().NotThrowAsync();
        queue.Occupancy.Should().Be(occupancyAfterFirstAck);
        message.Dispose();
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_WithCancelledToken_StillSettles()
    {
        using InMemoryTransportAdapter adapter = DlxAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m1");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        await adapter.SettleAsync(SettlementAction.Ack, message, new CancellationToken(canceled: true));

        queue.Occupancy.Should().Be(0);
        message.Dispose();
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_UndefinedAction_ThrowsAndKeepsDeliveryInFlight()
    {
        using InMemoryTransportAdapter adapter = DlxAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");
        Enqueue(queue, "m1");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        Func<Task> act = async () => await adapter.SettleAsync((SettlementAction)99, message);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        adapter.InFlight.Count.Should().Be(1); // never claimed

        await adapter.SettleAsync(SettlementAction.Ack, message); // still settleable afterwards
        queue.Occupancy.Should().Be(0);
        message.Dispose();
        await enumerator.DisposeAsync();
    }

    [Fact]
    public void SettleAsync_NullMessage_Throws()
    {
        using InMemoryTransportAdapter adapter = DlxAdapter();

        Func<Task> act = async () => await adapter.SettleAsync(SettlementAction.Ack, null!);

        act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Step 5: dead-letter fan-out, full DLQ, no DLX, unroutable DLX, routing-key override ─────────

    [Theory]
    [InlineData(SettlementAction.Nack)]
    [InlineData(SettlementAction.Reject)]
    public async Task SettleAsync_NackOrReject_RoutesCopyToDlqLikeRabbitMq(SettlementAction action)
    {
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter = DlxAdapter(observer);
        Enqueue(Queue(adapter, "orders"), "bad");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        await adapter.SettleAsync(action, message);
        message.Dispose();
        Queue(adapter, "orders").Occupancy.Should().Be(0);
        await enumerator.DisposeAsync();

        (InboundMessage dead, IAsyncEnumerator<InboundMessage> deadEnumerator) = await ReceiveOneAsync(adapter, "orders-dlq");
        BodyOf(dead).Should().Be("bad");
        dead.MessageId.Should().Be("bad");
        observer.IsOutstanding(dead.PooledBuffer!).Should().BeTrue(); // the settlement copy
        await adapter.SettleAsync(SettlementAction.Ack, dead);
        dead.Dispose();
        dead.PooledBuffer.Should().BeNull();
        observer.Violations.Should().BeEmpty();
        await deadEnumerator.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_Nack_WhenDlqFull_RejectsNewDeadLetterAndReturnsCopy()
    {
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter = DlxAdapter(observer, queueCapacity: 1);
        Enqueue(Queue(adapter, "orders-dlq"), "already-there"); // fills orders-dlq to capacity (1)
        Enqueue(Queue(adapter, "orders"), "bad");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        await adapter.SettleAsync(SettlementAction.Nack, message);

        observer.Rented.Should().Be(1);
        observer.Returned.Should().Be(1);
        observer.Violations.Should().BeEmpty();
        adapter.SettlementDroppedCount(SettlementDropReason.DeadLetterQueueFull).Should().Be(1);
        Queue(adapter, "orders").Occupancy.Should().Be(0);
        message.Dispose();
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_Nack_WithoutDlx_DropsWithNoDlxReason()
    {
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter = OrdersOnlyAdapter(observer);
        Enqueue(Queue(adapter, "orders"), "bad");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        await adapter.SettleAsync(SettlementAction.Nack, message);

        observer.Rented.Should().Be(0); // no target -> no copy
        adapter.SettlementDroppedCount(SettlementDropReason.NoDeadLetterExchange).Should().Be(1);
        Queue(adapter, "orders").Occupancy.Should().Be(0);
        message.Dispose();
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_Nack_WhenDlxHasNoBoundQueue_DropsWithUnroutableReason()
    {
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter = DlxUnroutableAdapter(observer);
        Enqueue(Queue(adapter, "orders"), "bad");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        await adapter.SettleAsync(SettlementAction.Nack, message);

        observer.Rented.Should().Be(0); // unroutable -> no copy
        adapter.SettlementDroppedCount(SettlementDropReason.Unroutable).Should().Be(1);
        Queue(adapter, "orders").Occupancy.Should().Be(0);
        message.Dispose();
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_Nack_WithDeadLetterRoutingKeyOverride_UsesOverrideKey()
    {
        using InMemoryTransportAdapter adapter = DirectDlxWithRoutingKeyOverrideAdapter();
        Enqueue(Queue(adapter, "orders"), "bad"); // stamped with routing key "orders" (the queue's name)
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        await adapter.SettleAsync(SettlementAction.Nack, message);
        message.Dispose();
        await enumerator.DisposeAsync();

        Queue(adapter, "orders-dlq").Occupancy.Should().Be(1); // landed despite the original key mismatch
        (InboundMessage dead, IAsyncEnumerator<InboundMessage> deadEnumerator) = await ReceiveOneAsync(adapter, "orders-dlq");
        BodyOf(dead).Should().Be("bad");
        await adapter.SettleAsync(SettlementAction.Ack, dead);
        dead.Dispose();
        await deadEnumerator.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_Nack_FanOutToTwoDlqs_EachGetsOwnBufferReturnedExactlyOnce()
    {
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter = FanOutTwoDlqAdapter(observer);
        Enqueue(Queue(adapter, "orders"), "bad");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        await adapter.SettleAsync(SettlementAction.Nack, message);
        message.Dispose();
        await enumerator.DisposeAsync();

        observer.Rented.Should().Be(2); // one copy per DLQ

        (InboundMessage dead1, IAsyncEnumerator<InboundMessage> e1) = await ReceiveOneAsync(adapter, "orders-dlq");
        (InboundMessage dead2, IAsyncEnumerator<InboundMessage> e2) = await ReceiveOneAsync(adapter, "orders-dlq-2");
        BodyOf(dead1).Should().Be("bad");
        BodyOf(dead2).Should().Be("bad");
        ReferenceEquals(dead1.PooledBuffer, dead2.PooledBuffer).Should().BeFalse();
        byte[] buffer1 = dead1.PooledBuffer!;
        byte[] buffer2 = dead2.PooledBuffer!;

        await adapter.SettleAsync(SettlementAction.Ack, dead1);
        dead1.Dispose();
        observer.MarkReturnedByMessage(buffer1);
        await adapter.SettleAsync(SettlementAction.Ack, dead2);
        dead2.Dispose();
        observer.MarkReturnedByMessage(buffer2);
        await e1.DisposeAsync();
        await e2.DisposeAsync();

        observer.Returned.Should().Be(2);
        observer.Outstanding.Should().Be(0);
        observer.Violations.Should().BeEmpty();
    }

    // M1: consuming (and disposing) the first DLQ's dead-letter immediately must never corrupt the copy
    // still being written to the second DLQ — proves the "extra copies first, first copy last" ordering.
    [Fact]
    public async Task SettleAsync_Nack_FirstDlqConsumedImmediately_SecondDlqContentStillCorrect()
    {
        using InMemoryTransportAdapter adapter = FanOutTwoDlqAdapter();
        Enqueue(Queue(adapter, "orders"), "bad");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        await adapter.SettleAsync(SettlementAction.Nack, message);
        message.Dispose();
        await enumerator.DisposeAsync();

        // Consume and immediately dispose the first DLQ's delivery — by the time this returns, the ordering
        // rule (M1) guarantees the second DLQ's copy is already independent of whichever buffer this one held.
        (InboundMessage dead1, IAsyncEnumerator<InboundMessage> e1) = await ReceiveOneAsync(adapter, "orders-dlq");
        await adapter.SettleAsync(SettlementAction.Ack, dead1);
        dead1.Dispose();
        await e1.DisposeAsync();

        (InboundMessage dead2, IAsyncEnumerator<InboundMessage> e2) = await ReceiveOneAsync(adapter, "orders-dlq-2");
        BodyOf(dead2).Should().Be("bad");
        await adapter.SettleAsync(SettlementAction.Ack, dead2);
        dead2.Dispose();
        await e2.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_Nack_AfterMessageDisposed_DropsWithoutRent()
    {
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter = DlxAdapter(observer);
        Enqueue(Queue(adapter, "orders"), "bad");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");
        message.Dispose(); // disposed BEFORE settlement — the buffer is already back in the pool

        await adapter.SettleAsync(SettlementAction.Nack, message);

        observer.Rented.Should().Be(0);
        adapter.DisposedUnsettledCount.Should().Be(1);
        Queue(adapter, "orders").Occupancy.Should().Be(0);
        await enumerator.DisposeAsync();
    }

    // ── Step 6: every rent returned exactly once across all settlement paths ─────────────────────

    [Fact]
    public async Task Settle_FanOutOfThree_EveryRentReturnedExactlyOnceAcrossAllPaths()
    {
        var observer = new CountingBufferPoolObserver();
        using InMemoryTransportAdapter adapter = FanOutThreeAdapter(observer);

        OutboundMessage published = ToExchange("events", routingKey: string.Empty, id: "evt-1", body: "payload");
        IReadOnlyList<SendResult> results =
            await adapter.SendBatchAsync([published], TestContext.Current.CancellationToken);
        results.Should().ContainSingle(r => r.IsConfirmed);

        // q1: Ack. The original buffer is now rented by the send path THROUGH the hook (the pool the
        // sender commits through is the adapter's own), so the observer sees the rent; the message's own
        // Dispose() still returns it directly to the shared pool, not through the hook, so the observer
        // must be told about that return explicitly — immediately, before the next settlement that rents.
        (InboundMessage m1, IAsyncEnumerator<InboundMessage> e1) = await ReceiveOneAsync(adapter, "q1");
        byte[]? sentBuffer = m1.PooledBuffer;
        await adapter.SettleAsync(SettlementAction.Ack, m1);
        m1.Dispose();
        m1.PooledBuffer.Should().BeNull();
        observer.MarkReturnedByMessage(sentBuffer!);
        await e1.DisposeAsync();

        // q2: Nack -> dead-letters to q2-dlq (a hook-rented copy) -> Ack. The original buffer is also
        // hook-rented now (the send path fans out through the pool), so its disposal must be marked too.
        (InboundMessage m2, IAsyncEnumerator<InboundMessage> e2) = await ReceiveOneAsync(adapter, "q2");
        byte[]? q2SentBuffer = m2.PooledBuffer;
        await adapter.SettleAsync(SettlementAction.Nack, m2);
        m2.Dispose();
        m2.PooledBuffer.Should().BeNull();
        observer.MarkReturnedByMessage(q2SentBuffer!);
        await e2.DisposeAsync();

        (InboundMessage dead2, IAsyncEnumerator<InboundMessage> deadEnumerator) = await ReceiveOneAsync(adapter, "q2-dlq");
        byte[]? deadBuffer = dead2.PooledBuffer;
        await adapter.SettleAsync(SettlementAction.Ack, dead2);
        dead2.Dispose();
        observer.MarkReturnedByMessage(deadBuffer!);
        await deadEnumerator.DisposeAsync();

        // q3: Requeue (a hook-rented copy) -> read the redelivery on the same enumerator -> Ack. The
        // original buffer is also hook-rented now, so its disposal must be marked too.
        (InboundMessage m3, IAsyncEnumerator<InboundMessage> e3) = await ReceiveOneAsync(adapter, "q3");
        byte[]? q3SentBuffer = m3.PooledBuffer;
        await adapter.SettleAsync(SettlementAction.Requeue, m3);
        m3.Dispose();
        m3.PooledBuffer.Should().BeNull();
        observer.MarkReturnedByMessage(q3SentBuffer!);
        (await e3.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        InboundMessage redelivered3 = e3.Current;
        byte[]? redeliveredBuffer = redelivered3.PooledBuffer;
        await adapter.SettleAsync(SettlementAction.Ack, redelivered3);
        redelivered3.Dispose();
        observer.MarkReturnedByMessage(redeliveredBuffer!);
        await e3.DisposeAsync();

        observer.Violations.Should().BeEmpty();
        observer.Outstanding.Should().Be(0);
        Queue(adapter, "q1").Occupancy.Should().Be(0);
        Queue(adapter, "q2").Occupancy.Should().Be(0);
        Queue(adapter, "q3").Occupancy.Should().Be(0);
        Queue(adapter, "q2-dlq").Occupancy.Should().Be(0);
    }

    // ── M7: Ack allocates nothing on the settling thread after warm-up ────────────────────────────

    [Fact]
    public async Task SettleAsync_Ack_AllocatesNothingAfterWarmup()
    {
        using InMemoryTransportAdapter adapter = OrdersOnlyAdapter();
        InMemoryQueue queue = Queue(adapter, "orders");

        // Warm-up: exercise every code path once (JIT, lazy static init, dictionary bucket allocation).
        Enqueue(queue, "warmup");
        (InboundMessage warmupMessage, IAsyncEnumerator<InboundMessage> warmupEnumerator) =
            await ReceiveOneAsync(adapter, "orders");
        await adapter.SettleAsync(SettlementAction.Ack, warmupMessage);
        warmupMessage.Dispose();
        await warmupEnumerator.DisposeAsync();

        Enqueue(queue, "m1");
        (InboundMessage message, IAsyncEnumerator<InboundMessage> enumerator) = await ReceiveOneAsync(adapter, "orders");

        long before = GC.GetAllocatedBytesForCurrentThread();
        await adapter.SettleAsync(SettlementAction.Ack, message);
        long after = GC.GetAllocatedBytesForCurrentThread();

        (after - before).Should().Be(0);
        message.Dispose();
        await enumerator.DisposeAsync();
    }

    // ── Test doubles ───────────────────────────────────────────────────────────────────────────────

    private sealed class WarningCountingLogger : ILogger
    {
        private int _warningCount;

        internal int WarningCount => Volatile.Read(ref _warningCount);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Interlocked.Increment(ref _warningCount);
            }
        }
    }
}
