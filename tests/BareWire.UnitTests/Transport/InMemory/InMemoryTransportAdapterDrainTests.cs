using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

/// <summary>
/// Covers <see cref="InMemoryTransportAdapter.Dispose"/>: closing every queue, waking senders waiting for
/// room with <c>closed</c>, cancelling deferred redeliveries, dropping what is left with a per-queue
/// warning and the <c>drain_dropped</c> metric, and returning every buffer exactly once — including when
/// a diagnostics listener itself throws.
/// </summary>
public sealed class InMemoryTransportAdapterDrainTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────────────

    private static InMemoryTransportAdapter Adapter(
        Action<IInMemoryConfigurator> configure,
        CountingBufferPoolObserver? observer = null,
        TimeProvider? timeProvider = null,
        Meter? meter = null,
        ILogger<InMemoryTransportAdapter>? logger = null)
    {
        var c = new InMemoryConfigurator();
        configure(c);
        InMemoryTransportOptions o = c.Build();
        return new InMemoryTransportAdapter(
            o, new InMemoryBroker(o), logger: logger, meter: meter, timeProvider: timeProvider,
            bufferPoolObserver: observer);
    }

    private static InMemoryQueue Queue(InMemoryTransportAdapter adapter, string name)
    {
        adapter.Broker.TryGetQueue(name, out InMemoryQueue? queue).Should().BeTrue();
        return queue!;
    }

    // message to the default exchange ("" via BW-Exchange) routed straight to queue `queue`
    private static OutboundMessage ToQueue(string queue, string id, int size = 8) =>
        new(queue, new Dictionary<string, string> { ["BW-Exchange"] = "", ["message-id"] = id }, new byte[size], "");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
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

    /// <summary>
    /// Attaches a <see cref="MeterListener"/> to <paramref name="meter"/>'s
    /// <see cref="InMemoryConsumeDiagnostics.DrainDroppedCounterName"/> instrument, appending every
    /// measurement (queue name, count) to <paramref name="measurements"/>. Dispose the returned listener
    /// (or let its <see langword="using"/> scope end) once the test is done observing.
    /// </summary>
    private static MeterListener ListenDrainDropped(Meter meter, List<(string Queue, long Count)> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name == InMemoryConsumeDiagnostics.DrainDroppedCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string queueName = string.Empty;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "queue")
                {
                    queueName = (string)tag.Value!;
                }
            }

            lock (measurements)
            {
                measurements.Add((queueName, value));
            }
        });
        listener.Start();
        return listener;
    }

    /// <summary>
    /// Attaches a <see cref="MeterListener"/> whose measurement callback throws for every
    /// <see cref="InMemoryConsumeDiagnostics.DrainDroppedCounterName"/> measurement — proves a listener's
    /// own failure never prevents buffers already returned before that point from staying returned.
    /// </summary>
    private static MeterListener ListenDrainDroppedThrowing(Meter meter)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name == InMemoryConsumeDiagnostics.DrainDroppedCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, _, _, _) => throw new InvalidOperationException("drain_dropped listener boom"));
        listener.Start();
        return listener;
    }

    // ILogger<T> that captures every entry, so a test can assert exactly which warnings were logged.
    private sealed class CapturingLogger : ILogger<InMemoryTransportAdapter>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        internal IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));
    }

    // ── DisposeAsync ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_WhenSenderWaitsForCapacity_CompletesSendWithClosedBeforeSendTimeout()
    {
        using InMemoryTransportAdapter adapter = Adapter(c =>
        {
            c.QueueCapacity(1);
            c.SendTimeout(TimeSpan.FromMinutes(5));
            c.ConfigureTopology(t => t.DeclareQueue("orders"));
        });

        IReadOnlyList<SendResult> seeded =
            await adapter.SendBatchAsync([ToQueue("orders", "m1")], TestContext.Current.CancellationToken);
        seeded.Should().ContainSingle(r => r.IsConfirmed);
        (InboundMessage held, IAsyncEnumerator<InboundMessage> holder) = await ReceiveOneAsync(adapter, "orders");

        Task<IReadOnlyList<SendResult>> send = adapter.SendBatchAsync([ToQueue("orders", "m2")]);
        await WaitUntilAsync(() => Queue(adapter, "orders").HasPendingSpaceWaiter);

        await adapter.DisposeAsync();

        IReadOnlyList<SendResult> results = await send.WaitAsync(TimeSpan.FromSeconds(5));
        results.Should().ContainSingle().Which.IsConfirmed.Should().BeFalse();
        Queue(adapter, "orders").HasPendingSpaceWaiter.Should().BeFalse();

        held.Dispose();
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WithQueuedAndDeferredMessages_ReturnsEveryRentalExactlyOnce()
    {
        var observer = new CountingBufferPoolObserver();
        var time = new FakeTimeProvider();
        InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.EnableDefer(TimeSpan.FromMinutes(1));
                c.ConfigureTopology(t =>
                {
                    t.DeclareQueue("orders");
                    t.DeclareQueue("audit");
                });
            },
            observer,
            time);

        await adapter.SendBatchAsync(
            [ToQueue("orders", "o1"), ToQueue("orders", "o2"), ToQueue("orders", "o3")],
            TestContext.Current.CancellationToken);
        await adapter.SendBatchAsync(
            [ToQueue("audit", "a1"), ToQueue("audit", "a2")],
            TestContext.Current.CancellationToken);

        (InboundMessage message, IAsyncEnumerator<InboundMessage> e) = await ReceiveOneAsync(adapter, "orders");
        byte[] consumed = message.PooledBuffer!;
        await adapter.SettleAsync(SettlementAction.Defer, message); // copy rented from the pool, pending timer
        message.Dispose();
        message.PooledBuffer.Should().BeNull();
        observer.MarkReturnedByMessage(consumed);

        await adapter.DisposeAsync();
        time.Advance(TimeSpan.FromMinutes(2)); // the cancelled timer writes nothing back

        adapter.PendingDeferCount.Should().Be(0);
        observer.Outstanding.Should().Be(0);
        observer.Violations.Should().BeEmpty();
        observer.Returned.Should().Be(observer.Rented);
        adapter.DrainDroppedCount.Should().Be(4); // 2 left on "orders" + 2 on "audit"
        Queue(adapter, "orders").Occupancy.Should().Be(0);
        Queue(adapter, "audit").Occupancy.Should().Be(0);

        await e.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WithQueuedMessages_RecordsDrainDroppedPerQueue()
    {
        using var meter = new Meter(
            "BareWire.UnitTests.InMemoryDrain." + Guid.NewGuid().ToString("N"));
        var logger = new CapturingLogger();
        InMemoryTransportAdapter adapter = Adapter(
            c => c.ConfigureTopology(t =>
            {
                t.DeclareQueue("orders");
                t.DeclareQueue("audit");
            }),
            meter: meter,
            logger: logger);

        var measurements = new List<(string Queue, long Count)>();
        using MeterListener listener = ListenDrainDropped(meter, measurements);

        await adapter.SendBatchAsync(
            [ToQueue("orders", "o1"), ToQueue("orders", "o2")], TestContext.Current.CancellationToken);
        await adapter.SendBatchAsync([ToQueue("audit", "a1")], TestContext.Current.CancellationToken);

        adapter.Dispose();

        measurements.Should().BeEquivalentTo([("orders", 2L), ("audit", 1L)]);

        IReadOnlyList<(LogLevel Level, string Message)> warnings =
            [.. logger.Entries.Where(static entry => entry.Level == LogLevel.Warning)];
        warnings.Should().HaveCount(2);
        warnings.Should().ContainSingle(w => w.Message.Contains("2 undelivered message(s) on queue 'orders'"));
        warnings.Should().ContainSingle(w => w.Message.Contains("1 undelivered message(s) on queue 'audit'"));
    }

    [Fact]
    public async Task DisposeAsync_WhenDiagnosticsListenerThrows_StillReturnsEveryBuffer()
    {
        var observer = new CountingBufferPoolObserver();
        using var meter = new Meter(
            "BareWire.UnitTests.InMemoryDrain." + Guid.NewGuid().ToString("N"));
        InMemoryTransportAdapter adapter = Adapter(
            c => c.ConfigureTopology(t => t.DeclareQueue("orders")),
            observer,
            meter: meter);

        using MeterListener listener = ListenDrainDroppedThrowing(meter);

        await adapter.SendBatchAsync(
            [ToQueue("orders", "o1"), ToQueue("orders", "o2")], TestContext.Current.CancellationToken);

        try
        {
            adapter.Dispose();
        }
        catch (Exception)
        {
            // The listener's own failure may propagate out of Dispose — acceptable, as long as every
            // buffer and slot this adapter owned was already released before that point.
        }

        observer.Outstanding.Should().Be(0);
        observer.Violations.Should().BeEmpty();
        Queue(adapter, "orders").Occupancy.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        await adapter.SendBatchAsync([ToQueue("orders", "o1")], TestContext.Current.CancellationToken);

        adapter.Dispose();
        long droppedAfterFirst = adapter.DrainDroppedCount;

        Action act = adapter.Dispose;

        act.Should().NotThrow();
        adapter.DrainDroppedCount.Should().Be(droppedAfterFirst);
    }

    // ── DrainAsync ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DrainAsync_WhenOnlyDeadLetterQueueHoldsMessages_CompletesWithoutWaitingForTimeout()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t =>
        {
            t.DeclareQueue("orders", true, false, q => q.DeadLetterExchange("dlx"));
            t.DeclareExchange("dlx", ExchangeType.Fanout);
            t.DeclareQueue("orders-dlq");
            t.BindExchangeToQueue("dlx", "orders-dlq", string.Empty);
        }));

        await adapter.SendBatchAsync(
            [ToQueue("orders", "o1"), ToQueue("orders", "o2")], TestContext.Current.CancellationToken);

        (InboundMessage m1, IAsyncEnumerator<InboundMessage> e) = await ReceiveOneAsync(adapter, "orders");
        await adapter.SettleAsync(SettlementAction.Nack, m1, TestContext.Current.CancellationToken);

        (await e.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        InboundMessage m2 = e.Current;
        await adapter.SettleAsync(SettlementAction.Nack, m2, TestContext.Current.CancellationToken);

        await adapter.DrainAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Queue(adapter, "orders-dlq").Occupancy.Should().Be(2);

        await e.DisposeAsync();
    }

    [Fact]
    public async Task DrainAsync_WhenActiveConsumerSettlesEverything_CompletesAfterQueueEmpties()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));

        await adapter.SendBatchAsync(
            [ToQueue("orders", "o1"), ToQueue("orders", "o2"), ToQueue("orders", "o3")],
            TestContext.Current.CancellationToken);

        // Start the drain BEFORE any message is settled: with an active consumer holding the first
        // delivery, IsDrained() is false on the first check, so the fast path cannot resolve synchronously.
        (InboundMessage m1, IAsyncEnumerator<InboundMessage> e) = await ReceiveOneAsync(adapter, "orders");
        Task drain = adapter.DrainAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        drain.IsCompleted.Should().BeFalse();

        await adapter.SettleAsync(SettlementAction.Ack, m1, TestContext.Current.CancellationToken);
        m1.Dispose();

        (await e.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        InboundMessage m2 = e.Current;
        await adapter.SettleAsync(SettlementAction.Ack, m2, TestContext.Current.CancellationToken);
        m2.Dispose();

        (await e.MoveNextAsync().AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        InboundMessage m3 = e.Current;
        await adapter.SettleAsync(SettlementAction.Ack, m3, TestContext.Current.CancellationToken);
        m3.Dispose();

        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Queue(adapter, "orders").Occupancy.Should().Be(0);

        await e.DisposeAsync();
    }

    [Fact]
    public async Task DrainAsync_WhenMessageDisposedWithoutSettlement_ReleasesSlotAndCompletes()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        await adapter.SendBatchAsync([ToQueue("orders", "o1")], TestContext.Current.CancellationToken);

        (InboundMessage message, IAsyncEnumerator<InboundMessage> e) = await ReceiveOneAsync(adapter, "orders");
        message.Dispose(); // the consumer released it without settling — SweepReleasedByConsumer claims it

        await adapter.DrainAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Queue(adapter, "orders").Occupancy.Should().Be(0);
        adapter.DisposedUnsettledCount.Should().Be(1);

        await e.DisposeAsync();
    }

    [Fact]
    public async Task DrainAsync_WhenActiveQueueNeverDrains_ReturnsAfterTimeoutWithoutThrowing()
    {
        var time = new FakeTimeProvider();
        InMemoryTransportAdapter adapter = Adapter(
            c => c.ConfigureTopology(t => t.DeclareQueue("orders")), timeProvider: time);
        await adapter.SendBatchAsync([ToQueue("orders", "o1")], TestContext.Current.CancellationToken);

        using var consumerCts = new CancellationTokenSource();
        (InboundMessage held, IAsyncEnumerator<InboundMessage> e) =
            await ReceiveOneAsync(adapter, "orders", consumerCts.Token);

        Task drain = adapter.DrainAsync(TimeSpan.FromSeconds(30));
        drain.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(31));

        await drain.WaitAsync(TimeSpan.FromSeconds(5)); // completes normally, no exception

        held.Dispose();
        await consumerCts.CancelAsync();
        await FluentActions.Awaiting(() => e.MoveNextAsync().AsTask()).Should().ThrowAsync<OperationCanceledException>();
        await e.DisposeAsync();
    }

    [Fact]
    public async Task DrainAsync_WhenTokenCancelled_ThrowsOperationCanceledException()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        await adapter.SendBatchAsync([ToQueue("orders", "o1")], TestContext.Current.CancellationToken);

        // An active consumer holds one delivery unsettled, so the drain cannot finish on its own.
        using var consumerCts = new CancellationTokenSource();
        (InboundMessage held, IAsyncEnumerator<InboundMessage> e) =
            await ReceiveOneAsync(adapter, "orders", consumerCts.Token);

        using var cts = new CancellationTokenSource();
        Task drain = adapter.DrainAsync(TimeSpan.FromMinutes(5), cts.Token);
        drain.IsCompleted.Should().BeFalse();

        await cts.CancelAsync();

        await FluentActions.Awaiting(() => drain.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();

        held.Dispose();
        await consumerCts.CancelAsync();
        await FluentActions.Awaiting(() => e.MoveNextAsync().AsTask()).Should().ThrowAsync<OperationCanceledException>();
        await e.DisposeAsync();
    }

    [Fact]
    public async Task DrainAsync_WithHugeTimeout_DoesNotThrow()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        await adapter.SendBatchAsync([ToQueue("orders", "o1")], TestContext.Current.CancellationToken);

        // An active consumer holds one delivery unsettled, so the huge-timeout drain cannot finish on its
        // own — the point of this test is that InMemoryQueue.MaxSupportedWaitTimeout clamping keeps the
        // underlying CancellationTokenSource from throwing ArgumentOutOfRangeException.
        using var consumerCts = new CancellationTokenSource();
        (InboundMessage held, IAsyncEnumerator<InboundMessage> e) =
            await ReceiveOneAsync(adapter, "orders", consumerCts.Token);

        using var cts = new CancellationTokenSource();
        Task drain = adapter.DrainAsync(TimeSpan.FromDays(100), cts.Token);

        drain.IsCompleted.Should().BeFalse();

        await cts.CancelAsync();
        await FluentActions.Awaiting(() => drain.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();

        held.Dispose();
        await consumerCts.CancelAsync();
        await FluentActions.Awaiting(() => e.MoveNextAsync().AsTask()).Should().ThrowAsync<OperationCanceledException>();
        await e.DisposeAsync();
    }

    [Fact]
    public void DrainAsync_WithNegativeTimeout_ThrowsArgumentOutOfRange()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));

        Action act = () => adapter.DrainAsync(TimeSpan.FromSeconds(-1), TestContext.Current.CancellationToken);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DrainAsync_AfterDispose_CompletesImmediately()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));
        adapter.Dispose();

        Task drain = adapter.DrainAsync(TimeSpan.FromMinutes(5));

        drain.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public void Adapter_ImplementsGracefulDrainTransport()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("orders")));

        adapter.Should().BeAssignableTo<IGracefulDrainTransport>();
    }
}
