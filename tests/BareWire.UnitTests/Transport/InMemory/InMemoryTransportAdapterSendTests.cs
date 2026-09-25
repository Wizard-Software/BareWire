using System.Buffers;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryTransportAdapterSendTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static InMemoryTransportAdapter Adapter(
        Action<IInMemoryConfigurator> configure,
        Meter? meter = null,
        ILogger<InMemoryTransportAdapter>? logger = null)
    {
        var c = new InMemoryConfigurator();
        configure(c);
        InMemoryTransportOptions o = c.Build();
        return new InMemoryTransportAdapter(o, new InMemoryBroker(o), logger: logger, meter: meter);
    }

    // ILogger<T> whose Log always throws, IsEnabled always true — proves a logging provider's own
    // failure (mitigation M11) never escapes the send path.
    private sealed class ThrowingLogger : ILogger<InMemoryTransportAdapter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger provider boom");
    }

    private static InMemoryQueue Queue(InMemoryTransportAdapter a, string name)
    {
        a.Broker.TryGetQueue(name, out InMemoryQueue? q).Should().BeTrue();
        return q!;
    }

    // message to the default exchange ("" via BW-Exchange) routed straight to queue `queue`
    private static OutboundMessage ToQueue(string queue, string id, int size = 8) =>
        new(queue, new Dictionary<string, string> { ["BW-Exchange"] = "", ["message-id"] = id }, new byte[size], "");

    private static OutboundMessage ToExchange(string exchange, string routingKey, string id) =>
        new(routingKey, new Dictionary<string, string> { ["BW-Exchange"] = exchange, ["message-id"] = id }, new byte[8], "");

    // fills `queue` to capacity by direct reservation (test-side setup, not the code under test)
    private static void Fill(InMemoryQueue queue)
    {
        while (queue.Occupancy < queue.Capacity)
        {
            queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(8);
            queue.WriteReserved(new InMemoryDelivery(buffer, 8, InMemoryHeaderSet.Empty));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        while (!condition())
        {
            await Task.Delay(1, cts.Token);
        }
    }

    // starts a consumer that reads one delivery (already sitting in the queue) and never settles it, so
    // the queue has an active consumer and the delivery's slot stays occupied outside the channel
    private static async Task<IAsyncEnumerator<InboundMessage>> HoldingConsumerAsync(
        InMemoryTransportAdapter a, string queue, CancellationToken ct)
    {
        IAsyncEnumerator<InboundMessage> e = a.ConsumeAsync(queue, new FlowControlOptions(), ct).GetAsyncEnumerator(ct);
        (await e.MoveNextAsync().AsTask().WaitAsync(Timeout, ct)).Should().BeTrue();
        return e;
    }

    // consumer that settles every delivery immediately: TryTakeInFlight + entry.Queue.ReleaseSlot() + message.Dispose()
    private static async Task FastConsumerAsync(InMemoryTransportAdapter a, string queue, CancellationToken ct)
    {
        IAsyncEnumerator<InboundMessage> e = a.ConsumeAsync(queue, new FlowControlOptions(), ct).GetAsyncEnumerator(ct);
        try
        {
            while (await e.MoveNextAsync())
            {
                InboundMessage message = e.Current;
                if (a.TryTakeInFlight(message, out InFlightDelivery entry))
                {
                    entry.Queue.ReleaseSlot();
                }

                message.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // cancellation is the normal way this loop ends
        }
        finally
        {
            await e.DisposeAsync();
        }
    }

    // MeterListener collecting (reason, queue/exchange tag) of "barewire.inmemory.messages.rejected"
    private static List<(string Reason, string? Queue, string? Exchange)> ListenRejected(Meter meter)
    {
        var recorded = new List<(string, string?, string?)>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == InMemoryTransportMetrics.RejectedCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? reason = null;
            string? queue = null;
            string? exchange = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                switch (tag.Key)
                {
                    case "reason": reason = tag.Value as string; break;
                    case "queue": queue = tag.Value as string; break;
                    case "exchange": exchange = tag.Value as string; break;
                }
            }

            for (long i = 0; i < value; i++)
            {
                recorded.Add((reason ?? string.Empty, queue, exchange));
            }
        });
        listener.Start();
        return recorded;
    }

    // ── Deliverable A: per-message validation, routing, 1:1 results, whole-call errors ───────────────

    [Fact]
    public async Task SendBatchAsync_WithOneOversizedMessage_RejectsOnlyThatMessage()
    {
        using var meter = new Meter("test." + Guid.NewGuid().ToString("N"));
        var rejected = ListenRejected(meter);
        InMemoryTransportAdapter a = Adapter(c => { c.ConfigureTopology(t => t.DeclareQueue("q")); c.MaxMessageSize(16); }, meter);

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToQueue("q", "1"), ToQueue("q", "2", size: 17), ToQueue("q", "3")], TestContext.Current.CancellationToken);

        r.Select(x => x.IsConfirmed).Should().Equal(true, false, true);
        Queue(a, "q").Occupancy.Should().Be(2);
        rejected.Should().ContainSingle(x => x.Reason == "oversized");
    }

    [Fact]
    public async Task SendBatchAsync_WithOneUndeclaredExchange_RejectsOnlyThatMessage()
    {
        using var meter = new Meter("test." + Guid.NewGuid().ToString("N"));
        var rejected = ListenRejected(meter);
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")), meter);

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToQueue("q", "1"), ToExchange("nope", "x", "2"), ToQueue("q", "3")], TestContext.Current.CancellationToken);

        r.Select(x => x.IsConfirmed).Should().Equal(true, false, true);
        rejected.Should().ContainSingle(x => x.Reason == "undeclared_exchange" && x.Exchange == null);
    }

    [Fact]
    public async Task SendBatchAsync_WithNoExchangeResolved_RejectsOnlyThatMessage()
    {
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        var noHeader = new OutboundMessage("q", new Dictionary<string, string>(), new byte[8], "");

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToQueue("q", "1"), noHeader], TestContext.Current.CancellationToken);

        r.Select(x => x.IsConfirmed).Should().Equal(true, false);
    }

    [Fact]
    public async Task SendBatchAsync_WithRoutingKeyOver255Bytes_ReturnsNotConfirmed()
    {
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        var tooLong = new OutboundMessage(new string('a', 256), new Dictionary<string, string> { ["BW-Exchange"] = "" }, new byte[8], "");

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([tooLong], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task SendBatchAsync_WithUnroutableMessage_ReturnsConfirmedByDefault()
    {
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareExchange("ex", ExchangeType.Direct)));

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([ToExchange("ex", "nowhere", "1")], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeTrue();
    }

    [Fact]
    public async Task SendBatchAsync_WithGuaranteedRouting_ReturnsNotConfirmed()
    {
        InMemoryTransportAdapter a = Adapter(c =>
        {
            c.GuaranteedRouting();
            c.ConfigureTopology(t => t.DeclareExchange("ex", ExchangeType.Direct));
        });

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([ToExchange("ex", "nowhere", "1")], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task SendBatchAsync_WithMixedBatch_ReturnsOneResultPerMessageInInputOrder()
    {
        InMemoryTransportAdapter a = Adapter(c => { c.ConfigureTopology(t => t.DeclareQueue("q")); c.MaxMessageSize(16); });

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToQueue("q", "1"), ToQueue("q", "2", size: 17), ToQueue("q", "3"),
                ToQueue("q", "4", size: 17), ToQueue("q", "5")],
            TestContext.Current.CancellationToken);

        r.Should().HaveCount(5);
        r.Select(x => x.IsConfirmed).Should().Equal(true, false, true, false, true);
        r.Select(x => x.DeliveryTag).Distinct().Should().HaveCount(5);
        r.Select(x => x.DeliveryTag).Should().BeInAscendingOrder();
    }

    private sealed class ThrowingHeaders : IReadOnlyDictionary<string, string>
    {
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => throw new InvalidOperationException("boom");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public int Count => throw new InvalidOperationException("boom");
        public bool ContainsKey(string key) => throw new InvalidOperationException("boom");
        public bool TryGetValue(string key, out string value) => throw new InvalidOperationException("boom");
        public string this[string key] => throw new InvalidOperationException("boom");
        public IEnumerable<string> Keys => throw new InvalidOperationException("boom");
        public IEnumerable<string> Values => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task SendBatchAsync_WhenHeaderEnumerationThrows_ReturnsInternalErrorForThatMessageOnly()
    {
        using var meter = new Meter("test." + Guid.NewGuid().ToString("N"));
        var rejected = ListenRejected(meter);
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")), meter);
        var throwing = new OutboundMessage("q", new ThrowingHeaders(), new byte[8], "");

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToQueue("q", "1"), throwing, ToQueue("q", "3")], TestContext.Current.CancellationToken);

        r.Select(x => x.IsConfirmed).Should().Equal(true, false, true);
        rejected.Should().ContainSingle(x => x.Reason == "internal_error");
    }

    [Fact]
    public void SendBatchAsync_WithNullBatch_ThrowsArgumentNullException()
    {
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")));

        Action act = () => a.SendBatchAsync(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SendBatchAsync_WithNullElement_ThrowsArgumentNullExceptionAndEnqueuesNothing()
    {
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")));

        Action act = () => a.SendBatchAsync([ToQueue("q", "1"), null!]);

        act.Should().Throw<ArgumentNullException>();
        Queue(a, "q").Occupancy.Should().Be(0);
    }

    [Fact]
    public void SendBatchAsync_WithCancelledTokenOnEntry_ThrowsOperationCanceledExceptionAndEnqueuesNothing()
    {
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Action act = () => a.SendBatchAsync([ToQueue("q", "1")], cts.Token);

        act.Should().Throw<OperationCanceledException>();
        Queue(a, "q").Occupancy.Should().Be(0);
    }

    [Fact]
    public async Task SendBatchAsync_AfterDispose_ReturnsClosedForEveryMessage()
    {
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        await a.DisposeAsync();

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToQueue("q", "1"), ToQueue("q", "2")], TestContext.Current.CancellationToken);

        r.Select(x => x.IsConfirmed).Should().Equal(false, false);
    }

    [Fact]
    public void SendBatchAsync_WhenAllCopiesFit_CompletesSynchronously()
    {
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")));

        Task<IReadOnlyList<SendResult>> task = a.SendBatchAsync([ToQueue("q", "1")], TestContext.Current.CancellationToken);

        task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task SendBatchAsync_WithBwExchangeInDifferentCase_IgnoresItAndUsesDefaultExchange()
    {
        InMemoryTransportAdapter a = Adapter(c =>
        {
            c.DefaultExchange("");
            c.ConfigureTopology(t => t.DeclareQueue("q"));
        });
        var lowerCaseKeyed = new OutboundMessage(
            "q", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["bw-exchange"] = "does-not-exist" },
            new byte[8], "");

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([lowerCaseKeyed], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeTrue();
        Queue(a, "q").Occupancy.Should().Be(1);
    }

    [Fact]
    public async Task SendBatchAsync_WithAcceptedMessage_DeliversBodyCopyAndStampedHeaders()
    {
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        byte[] body = [1, 2, 3, 4];

        await a.SendBatchAsync(
            [new OutboundMessage("q", new Dictionary<string, string> { ["BW-Exchange"] = "" }, body, "application/json")],
            TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<InboundMessage> consumer =
            a.ConsumeAsync("q", new FlowControlOptions(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await consumer.MoveNextAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken)).Should().BeTrue();
        InboundMessage message = consumer.Current;

        message.Body.ToArray().Should().Equal(body);
        message.Headers[InMemoryHeaderNames.Exchange].Should().Be("");
        message.Headers[InMemoryHeaderNames.RoutingKey].Should().Be("q");
    }

    // ── Deliverable B: per-queue reservation, fan-out partial delivery, latch without a consumer ─────

    [Fact]
    public async Task SendBatchAsync_FanOutWithOneLatchedQueue_DeliversToOthersAndReturnsNotConfirmed()
    {
        InMemoryTransportAdapter a = Adapter(c => { c.QueueCapacity(2); c.ConfigureTopology(t =>
        {
            t.DeclareExchange("f", ExchangeType.Fanout);
            t.DeclareQueue("q1"); t.DeclareQueue("q2"); t.DeclareQueue("q3");
            t.BindExchangeToQueue("f", "q1", ""); t.BindExchangeToQueue("f", "q2", ""); t.BindExchangeToQueue("f", "q3", "");
        }); });
        Fill(Queue(a, "q2"));
        Queue(a, "q2").TryReserve().Should().Be(QueueReservationResult.Latched); // full + no consumer => latched

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([ToExchange("f", "", "m")], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeFalse();
        Queue(a, "q1").Occupancy.Should().Be(1);
        Queue(a, "q3").Occupancy.Should().Be(1);
        Queue(a, "q2").Occupancy.Should().Be(2);
    }

    [Fact]
    public async Task SendBatchAsync_ToFullQueueWithoutConsumer_LatchesAndRejectsWithoutWaiting()
    {
        InMemoryTransportAdapter a = Adapter(c => { c.QueueCapacity(1); c.SendTimeout(TimeSpan.FromSeconds(10));
            c.ConfigureTopology(t => t.DeclareQueue("q")); });
        Fill(Queue(a, "q"));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([ToQueue("q", "1")], TestContext.Current.CancellationToken)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken);

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        r.Single().IsConfirmed.Should().BeFalse();
        Queue(a, "q").IsLatched.Should().BeTrue();
    }

    [Fact]
    public async Task SendBatchAsync_ToLatchedQueue_RejectsImmediatelyWithQueueFullReason()
    {
        using var meter = new Meter("test." + Guid.NewGuid().ToString("N"));
        var rejected = ListenRejected(meter);
        InMemoryTransportAdapter a = Adapter(c => { c.QueueCapacity(1); c.ConfigureTopology(t => t.DeclareQueue("q")); }, meter);
        Fill(Queue(a, "q"));
        Queue(a, "q").TryReserve().Should().Be(QueueReservationResult.Latched);

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([ToQueue("q", "1")], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeFalse();
        rejected.Should().ContainSingle(x => x.Reason == "queue_full" && x.Queue == "q");
    }

    // ── Deliverable C: one wait per call in the SendTimeout budget, latch after a failed wait ────────

    [Fact]
    public async Task SendBatchAsync_TwoFullQueuesWithActiveConsumers_WaitsOnceAndLatchesOnlyTheWaitedQueue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InMemoryTransportAdapter a = Adapter(c => { c.QueueCapacity(1); c.SendTimeout(TimeSpan.FromMilliseconds(200));
            c.ConfigureTopology(t => { t.DeclareQueue("q1"); t.DeclareQueue("q2"); }); });
        await a.SendBatchAsync([ToQueue("q1", "a"), ToQueue("q2", "b")], ct);          // fill both
        await using IAsyncEnumerator<InboundMessage> h1 = await HoldingConsumerAsync(a, "q1", ct);  // active, never settles
        await using IAsyncEnumerator<InboundMessage> h2 = await HoldingConsumerAsync(a, "q2", ct);

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([ToQueue("q1", "c"), ToQueue("q2", "d")], ct).WaitAsync(Timeout, ct);

        r.Select(x => x.IsConfirmed).Should().Equal(false, false);
        Queue(a, "q1").IsLatched.Should().BeTrue();   // the one wait failed => latch
        Queue(a, "q2").IsLatched.Should().BeFalse();  // rejected only because the call had already waited
    }

    [Fact]
    public async Task SendBatchAsync_WithZeroSendTimeoutAndFullQueue_RejectsImmediatelyWithoutLatch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InMemoryTransportAdapter a = Adapter(c => { c.QueueCapacity(1); c.SendTimeout(TimeSpan.Zero);
            c.ConfigureTopology(t => t.DeclareQueue("q")); });
        await a.SendBatchAsync([ToQueue("q", "a")], ct);
        await using IAsyncEnumerator<InboundMessage> h = await HoldingConsumerAsync(a, "q", ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([ToQueue("q", "b")], ct).WaitAsync(Timeout, ct);

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        r.Single().IsConfirmed.Should().BeFalse();
        Queue(a, "q").IsLatched.Should().BeFalse();
    }

    [Fact]
    public async Task SendBatchAsync_WhenSlotFreedDuringWait_AcceptsMessage()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InMemoryTransportAdapter a = Adapter(c => { c.QueueCapacity(1); c.SendTimeout(TimeSpan.FromSeconds(10));
            c.ConfigureTopology(t => t.DeclareQueue("q")); });
        await a.SendBatchAsync([ToQueue("q", "a")], ct);
        IAsyncEnumerator<InboundMessage> h = await HoldingConsumerAsync(a, "q", ct);

        Task<IReadOnlyList<SendResult>> sendTask = a.SendBatchAsync([ToQueue("q", "b")], ct);
        await WaitUntilAsync(() => Queue(a, "q").HasPendingSpaceWaiter, ct);
        a.TryTakeInFlight(h.Current, out InFlightDelivery entry).Should().BeTrue();
        entry.Queue.ReleaseSlot();
        h.Current.Dispose();
        await h.DisposeAsync();

        IReadOnlyList<SendResult> r = await sendTask.WaitAsync(Timeout, ct);

        r.Single().IsConfirmed.Should().BeTrue();
        Queue(a, "q").IsLatched.Should().BeFalse();
    }

    [Fact]
    public async Task SendBatchAsync_BurstOfTwiceCapacityWithFastConsumer_NeverLatches()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InMemoryTransportAdapter a = Adapter(c => { c.QueueCapacity(10); c.SendTimeout(TimeSpan.FromSeconds(5));
            c.ConfigureTopology(t => t.DeclareQueue("q")); });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task consumer = FastConsumerAsync(a, "q", cts.Token);
        await WaitUntilAsync(() => Queue(a, "q").HasActiveConsumer, ct);

        for (int i = 0; i < 20; i++)
        {
            string id = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{i}");
            IReadOnlyList<SendResult> r = await a.SendBatchAsync([ToQueue("q", id)], ct).WaitAsync(Timeout, ct);
            r.Single().IsConfirmed.Should().BeTrue();
        }

        await cts.CancelAsync();
        await consumer.WaitAsync(Timeout, ct);
        Queue(a, "q").IsLatched.Should().BeFalse();
    }

    // ── Deliverable D: cancellation while the call's one wait is pending ─────────────────────────────

    [Fact]
    public async Task SendBatchAsync_CancelledDuringWait_ReturnsCancelledWithoutExceptionOrDuplicates()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InMemoryTransportAdapter a = Adapter(c => { c.QueueCapacity(1); c.SendTimeout(TimeSpan.FromSeconds(30));
            c.ConfigureTopology(t => { t.DeclareQueue("free"); t.DeclareQueue("full"); }); });
        await a.SendBatchAsync([ToQueue("full", "x")], ct);
        await using IAsyncEnumerator<InboundMessage> h = await HoldingConsumerAsync(a, "full", ct);
        using var cts = new CancellationTokenSource();

        Task<IReadOnlyList<SendResult>> sendTask = a.SendBatchAsync(
            [ToQueue("free", "1"), ToQueue("full", "2"), ToQueue("free", "3")], cts.Token);
        await WaitUntilAsync(() => Queue(a, "full").HasPendingSpaceWaiter, ct);
        await cts.CancelAsync();

        IReadOnlyList<SendResult> r = await sendTask.WaitAsync(Timeout, ct);

        r.Select(x => x.IsConfirmed).Should().Equal(true, false, false);
        Queue(a, "free").Occupancy.Should().Be(1);     // "1" once, "3" never sent
        Queue(a, "full").IsLatched.Should().BeFalse(); // cancellation is not a failed wait
    }

    [Fact]
    public async Task SendBatchAsync_WithMaxValueSendTimeoutCancelledDuringWait_ReturnsNotConfirmedWithoutException()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InMemoryTransportAdapter a = Adapter(c => { c.QueueCapacity(1); c.SendTimeout(TimeSpan.MaxValue);
            c.ConfigureTopology(t => t.DeclareQueue("q")); });
        await a.SendBatchAsync([ToQueue("q", "a")], ct);
        await using IAsyncEnumerator<InboundMessage> h = await HoldingConsumerAsync(a, "q", ct);
        using var cts = new CancellationTokenSource();

        Task<IReadOnlyList<SendResult>> sendTask = a.SendBatchAsync([ToQueue("q", "b")], cts.Token);
        await WaitUntilAsync(() => Queue(a, "q").HasPendingSpaceWaiter, ct);
        await cts.CancelAsync();

        IReadOnlyList<SendResult> r = await sendTask.WaitAsync(Timeout, ct);

        r.Single().IsConfirmed.Should().BeFalse();
        Queue(a, "q").LinkedWaiterCount.Should().Be(0);
    }

    [Fact]
    public async Task SendBatchAsync_TwoFullQueuesForOneFanOutMessage_LatchesBothAfterSingleWindow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InMemoryTransportAdapter a = Adapter(c => { c.QueueCapacity(1); c.SendTimeout(TimeSpan.FromMilliseconds(200));
            c.ConfigureTopology(t =>
            {
                t.DeclareExchange("f", ExchangeType.Fanout);
                t.DeclareQueue("q1"); t.DeclareQueue("q2");
                t.BindExchangeToQueue("f", "q1", ""); t.BindExchangeToQueue("f", "q2", "");
            }); });
        await a.SendBatchAsync([ToExchange("f", "", "seed")], ct); // fill both
        await using IAsyncEnumerator<InboundMessage> h1 = await HoldingConsumerAsync(a, "q1", ct);
        await using IAsyncEnumerator<InboundMessage> h2 = await HoldingConsumerAsync(a, "q2", ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([ToExchange("f", "", "m")], ct).WaitAsync(Timeout, ct);

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        r.Single().IsConfirmed.Should().BeFalse();
        Queue(a, "q1").IsLatched.Should().BeTrue();
        Queue(a, "q2").IsLatched.Should().BeTrue();
    }

    // ── Deliverable E: many concurrent writers ────────────────────────────────────────────────────────

    [Fact]
    public async Task SendBatchAsync_WithConcurrentWriters_CompletesWithoutDeadlockAndKeepsOccupancyConsistent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InMemoryTransportAdapter a = Adapter(c => { c.QueueCapacity(4); c.SendTimeout(TimeSpan.FromMilliseconds(50));
            c.ConfigureTopology(t =>
            {
                t.DeclareExchange("f", ExchangeType.Fanout);
                t.DeclareQueue("q1"); t.DeclareQueue("q2");
                t.BindExchangeToQueue("f", "q1", ""); t.BindExchangeToQueue("f", "q2", "");
            }); });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task consumer1 = FastConsumerAsync(a, "q1", cts.Token);
        Task consumer2 = FastConsumerAsync(a, "q2", cts.Token);
        await WaitUntilAsync(() => Queue(a, "q1").HasActiveConsumer && Queue(a, "q2").HasActiveConsumer, ct);

        async Task WriterAsync(int writerId)
        {
            for (int i = 0; i < 50; i++)
            {
                string prefix = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture, $"{writerId}-{i}-");
                IReadOnlyList<SendResult> r = await a.SendBatchAsync(
                    [ToExchange("f", "", prefix + "a"), ToExchange("f", "", prefix + "b"), ToExchange("f", "", prefix + "c")],
                    ct);
                r.Should().HaveCount(3);
            }
        }

        Task[] writers = Enumerable.Range(0, 8).Select(WriterAsync).ToArray();
        await Task.WhenAll(writers).WaitAsync(Timeout, ct);

        // let the consumers drain every accepted copy before stopping them; cancelling earlier would leave
        // deliveries in the channel (their slots legitimately still occupied)
        await WaitUntilAsync(() => Queue(a, "q1").Occupancy == 0 && Queue(a, "q2").Occupancy == 0, ct);
        await cts.CancelAsync();
        await Task.WhenAll(consumer1, consumer2).WaitAsync(Timeout, ct);

        Queue(a, "q1").Occupancy.Should().Be(0);
        Queue(a, "q2").Occupancy.Should().Be(0);
        Queue(a, "q1").LinkedWaiterCount.Should().Be(0);
        Queue(a, "q2").LinkedWaiterCount.Should().Be(0);
        Queue(a, "q1").HasPendingSpaceWaiter.Should().BeFalse();
        Queue(a, "q2").HasPendingSpaceWaiter.Should().BeFalse();
    }

    // ── Regression: throwing logger, zero-length body, oversized-to-undeclared-exchange tag order ────

    [Fact]
    public async Task SendBatchAsync_WithThrowingLogger_ReturnsResultsWithoutPropagatingLoggerException()
    {
        InMemoryTransportAdapter a = Adapter(
            c => { c.ConfigureTopology(t => t.DeclareQueue("q")); c.MaxMessageSize(16); },
            logger: new ThrowingLogger());

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToQueue("q", "1", size: 17), ToQueue("q", "2")], TestContext.Current.CancellationToken);

        r.Select(x => x.IsConfirmed).Should().Equal(false, true);
        Queue(a, "q").Occupancy.Should().Be(1);
    }

    [Fact]
    public async Task SendBatchAsync_WithZeroLengthBody_DeliversMessageWithEmptyBody()
    {
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        var empty = new OutboundMessage("q", new Dictionary<string, string> { ["BW-Exchange"] = "" }, Array.Empty<byte>(), "");

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([empty], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeTrue();
        Queue(a, "q").Occupancy.Should().Be(1);

        await using IAsyncEnumerator<InboundMessage> consumer =
            a.ConsumeAsync("q", new FlowControlOptions(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await consumer.MoveNextAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken)).Should().BeTrue();

        consumer.Current.Body.Length.Should().Be(0);
    }

    [Fact]
    public async Task SendBatchAsync_WithOversizedMessageToUndeclaredExchange_RejectsAsOversizedWithoutExchangeTag()
    {
        using var meter = new Meter("test." + Guid.NewGuid().ToString("N"));
        var rejected = ListenRejected(meter);
        InMemoryTransportAdapter a = Adapter(c => c.MaxMessageSize(16), meter);
        var oversizedToUndeclared = new OutboundMessage(
            "rk", new Dictionary<string, string> { ["BW-Exchange"] = "undeclared" }, new byte[32], "");

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([oversizedToUndeclared], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeFalse();
        rejected.Should().ContainSingle(x => x.Reason == "oversized" && x.Exchange == null);
    }
}
