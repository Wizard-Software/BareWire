using System.Diagnostics.Metrics;
using System.Text;
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

/// <summary>
/// Covers <see cref="InMemoryTransportMetrics"/> — the single owner of every in-memory transport
/// instrument — directly (gauges, the shared rejected-messages counter, the null-meter no-op path) and
/// through the adapter's send path (one measurement per rejection reason, with the correct tag set), plus
/// the explicit catch-and-count guards added to <see cref="InMemoryConsumeDiagnostics"/> and
/// <see cref="InMemoryRouter"/> so a throwing metrics listener or logging provider can never crash a
/// caller or double-count a rejection.
/// </summary>
public sealed class InMemoryTransportMetricsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ── Adapter-level fixtures (mirrors InMemoryTransportAdapterSendTests) ───────────────────────────

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

    private static InMemoryQueue Queue(InMemoryTransportAdapter a, string name)
    {
        a.Broker.TryGetQueue(name, out InMemoryQueue? q).Should().BeTrue();
        return q!;
    }

    // message to the default exchange ("" via BW-Exchange) routed straight to queue `queue`
    private static OutboundMessage ToQueue(string queue, string id, int size = 8) =>
        new(queue, new Dictionary<string, string> { ["BW-Exchange"] = "", ["message-id"] = id }, new byte[size], "");

    private static OutboundMessage ToExchange(string exchange, string routingKey, string id, int size = 8) =>
        new(routingKey, new Dictionary<string, string> { ["BW-Exchange"] = exchange, ["message-id"] = id }, new byte[size], "");

    // fills `queue` to capacity by direct reservation (test-side setup, not the code under test)
    private static void Fill(InMemoryQueue queue)
    {
        while (queue.Occupancy < queue.Capacity)
        {
            queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8);
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

    // MeterListener collecting (reason, queue/exchange tag) of InMemoryTransportMetrics.RejectedCounterName
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

    // ILogger<T> whose Log always throws, IsEnabled always true — proves a logging provider's own
    // failure never escapes any of the explicit catch-and-count guards under test here.
    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger provider boom");
    }

    // ── InMemoryTransportMetrics: gauges, the shared counter, the null-meter no-op path ──────────────

    [Fact]
    public void ObservableGauges_WhenCollected_ReportOccupancyCapacityAndLatchPerQueue()
    {
        using var meter = new Meter("test-" + Guid.NewGuid());
        var queue = new InMemoryQueue("orders", capacity: 4);
        for (int i = 0; i < 4; i++)
        {
            queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        }

        queue.TryReserve().Should().Be(QueueReservationResult.Latched); // full, no consumer => latched

        var seen = new List<(string Name, int Value, string Queue)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            string queueTag = string.Empty;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "queue")
                {
                    queueTag = (string)tag.Value!;
                }
            }

            seen.Add((instrument.Name, value, queueTag));
        });
        listener.Start();

        _ = new InMemoryTransportMetrics(meter, [queue]);
        listener.RecordObservableInstruments();

        seen.Should().Contain((InMemoryTransportMetrics.OccupancyGaugeName, 4, "orders"));
        seen.Should().Contain((InMemoryTransportMetrics.CapacityGaugeName, 4, "orders"));
        seen.Should().Contain((InMemoryTransportMetrics.LatchedGaugeName, 1, "orders"));
    }

    [Fact]
    public void RecordExchangeRejected_WhenExchangeUndeclared_EmitsReasonTagOnly()
    {
        using var meter = new Meter("test-" + Guid.NewGuid());
        var measurements = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == InMemoryTransportMetrics.RejectedCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => measurements.Add(tags.ToArray()));
        listener.Start();

        var metrics = new InMemoryTransportMetrics(meter, []);
        metrics.RecordExchangeRejected("undeclared_exchange", declaredExchange: null);

        KeyValuePair<string, object?>[] tags = measurements.Should().ContainSingle().Which;
        tags.Select(static t => t.Key).Should().BeEquivalentTo(["reason"]);
        tags.Single().Value.Should().Be("undeclared_exchange");
    }

    [Fact]
    public void Constructor_WhenMeterIsNull_CreatesNoInstrumentsAndRecordIsNoOp()
    {
        var queue = new InMemoryQueue("q", capacity: 4);

        var metrics = new InMemoryTransportMetrics(null, [queue]);

        Action act = () =>
        {
            metrics.RecordQueueRejected("queue_full", "q");
            metrics.RecordExchangeRejected("unroutable", "ex");
            metrics.RecordExchangeRejected("no_exchange", null);
            metrics.RecordRejected("closed");
            metrics.RecordLatchEpisode("q");
        };

        act.Should().NotThrow();
    }

    // ── Adapter send path: one measurement per rejection reason, with the correct tag set ────────────

    [Fact]
    public async Task SendBatchAsync_WhenBodyOversized_CountsOversizedWithDeclaredExchangeTag()
    {
        using var meter = new Meter("test-" + Guid.NewGuid());
        var rejected = ListenRejected(meter);
        InMemoryTransportAdapter a = Adapter(c =>
        {
            c.MaxMessageSize(16);
            c.ConfigureTopology(t =>
            {
                t.DeclareExchange("ex", ExchangeType.Direct);
                t.DeclareQueue("q");
                t.BindExchangeToQueue("ex", "q", "k");
            });
        }, meter);

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToExchange("ex", "k", "1", size: 17)], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeFalse();
        rejected.Should().ContainSingle(x => x.Reason == "oversized" && x.Exchange == "ex");
    }

    [Fact]
    public async Task SendBatchAsync_WhenExchangeUndeclared_CountsUndeclaredExchangeWithoutExchangeTag()
    {
        using var meter = new Meter("test-" + Guid.NewGuid());
        var rejected = ListenRejected(meter);
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")), meter);

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToExchange("nope", "k", "1")], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeFalse();
        rejected.Should().ContainSingle(x => x.Reason == "undeclared_exchange" && x.Exchange == null);
    }

    [Fact]
    public async Task SendBatchAsync_WhenNoExchangeAndNoDefault_CountsNoExchange()
    {
        using var meter = new Meter("test-" + Guid.NewGuid());
        var rejected = ListenRejected(meter);
        InMemoryTransportAdapter a = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")), meter);
        var noHeader = new OutboundMessage("q", new Dictionary<string, string>(), new byte[8], "");

        IReadOnlyList<SendResult> r = await a.SendBatchAsync([noHeader], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeFalse();
        rejected.Should().ContainSingle(x => x.Reason == "no_exchange" && x.Queue == null && x.Exchange == null);
    }

    [Fact]
    public async Task SendBatchAsync_WhenCancelledDuringWait_CountsCancelled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var meter = new Meter("test-" + Guid.NewGuid());
        var rejected = ListenRejected(meter);
        InMemoryTransportAdapter a = Adapter(c =>
        {
            c.QueueCapacity(1);
            c.SendTimeout(TimeSpan.FromSeconds(30));
            c.ConfigureTopology(t => t.DeclareQueue("q"));
        }, meter);
        await a.SendBatchAsync([ToQueue("q", "x")], ct);
        await using IAsyncEnumerator<InboundMessage> h = await HoldingConsumerAsync(a, "q", ct);
        using var cts = new CancellationTokenSource();

        Task<IReadOnlyList<SendResult>> sendTask = a.SendBatchAsync([ToQueue("q", "2")], cts.Token);
        await WaitUntilAsync(() => Queue(a, "q").HasPendingSpaceWaiter, ct);
        await cts.CancelAsync();

        IReadOnlyList<SendResult> r = await sendTask.WaitAsync(Timeout, ct);

        r.Single().IsConfirmed.Should().BeFalse();
        rejected.Should().ContainSingle(x => x.Reason == "cancelled");
    }

    [Fact]
    public async Task SendBatchAsync_WhenFanOutCopyRejected_CountsQueueFullPerRejectedCopy()
    {
        using var meter = new Meter("test-" + Guid.NewGuid());
        var rejected = ListenRejected(meter);
        InMemoryTransportAdapter a = Adapter(c =>
        {
            c.QueueCapacity(1);
            c.ConfigureTopology(t =>
            {
                t.DeclareExchange("f", ExchangeType.Fanout);
                t.DeclareQueue("q1");
                t.DeclareQueue("q2");
                t.BindExchangeToQueue("f", "q1", "");
                t.BindExchangeToQueue("f", "q2", "");
            });
        }, meter);
        Fill(Queue(a, "q2"));
        Queue(a, "q2").TryReserve().Should().Be(QueueReservationResult.Latched); // full, no consumer => latched

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToExchange("f", "", "m")], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeFalse();
        rejected.Should().ContainSingle(x => x.Reason == "queue_full" && x.Queue == "q2");
    }

    // ── Cardinality / secrecy: no instrument or log ever carries publisher-controlled content ────────

    /// <summary>
    /// Drives every currently-implemented rejection path (<c>oversized</c>, <c>undeclared_exchange</c>,
    /// <c>unroutable</c>, <c>queue_full</c>, <c>closed</c>) plus a full latch-episode/health-transition
    /// cycle, all carrying the same set of sentinel values a publisher fully controls — a routing key, a
    /// <c>message-id</c> header, another header, and body bytes — plus an UNDECLARED exchange name. Then
    /// asserts the cardinality rule from <see cref="InMemoryTransportMetrics"/>'s own remarks holds for
    /// every measurement of every <c>barewire.inmemory.*</c> instrument (only <c>queue</c>/<c>exchange</c>/
    /// <c>reason</c> tag keys, never more than one of <c>queue</c>/<c>exchange</c>, no sentinel value —
    /// including the undeclared exchange name — ever becomes a tag), and that no captured log entry's
    /// formatted message or structured state ever carries the <c>message-id</c>, other-header, or body
    /// sentinel. The routing key and the exchange name (declared or not) are deliberately excluded from
    /// that log check: <see cref="InMemorySendDiagnostics.MessageRejected"/> and
    /// <see cref="InMemoryRouter.ReportUnroutable"/> both log those two as metadata by design — including
    /// falling back to the publisher-supplied, undeclared exchange name for the log text specifically
    /// when no declared one exists (see <see cref="InMemorySendDiagnostics.MessageRejected"/>'s own
    /// remarks on its <c>publisherExchange</c> parameter) — while still never letting either become a
    /// metric tag or a throttle key.
    /// </summary>
    [Fact]
    public async Task RejectedCounter_ForEveryReason_NeverCarriesRoutingKeyOrMessageId()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        const string RoutingKeySentinel = "rk-7f3a-secret";
        const string MessageIdSentinel = "mid-9c1e";
        const string OtherHeaderSentinel = "hdr-5b2d";
        const string BodySentinel = "body-8e4f";
        const string UndeclaredExchangeSentinel = "undeclared-x-3c7a";
        string[] sentinels = [RoutingKeySentinel, MessageIdSentinel, OtherHeaderSentinel, BodySentinel, UndeclaredExchangeSentinel];

        using var meter = new Meter("test-" + Guid.NewGuid());
        var logger = new CapturingLogger<InMemoryTransportAdapter>();
        InMemoryTransportAdapter adapter = Adapter(c =>
        {
            c.MaxMessageSize(16);
            c.QueueCapacity(1);
            c.ConfigureTopology(t =>
            {
                // Bound to a DIFFERENT key than the sentinel routing key, so a message carrying the
                // sentinel never matches this binding — driving the "unroutable" path deliberately.
                t.DeclareExchange("ex", ExchangeType.Direct);
                t.DeclareQueue("bound-q");
                t.BindExchangeToQueue("ex", "bound-q", "bound-key");

                t.DeclareQueue("qFull");
            });
        }, meter, logger);

        Dictionary<string, string> SentinelHeaders(string exchange) => new()
        {
            ["BW-Exchange"] = exchange,
            ["message-id"] = MessageIdSentinel,
            ["X-Sentinel-Header"] = OtherHeaderSentinel,
        };

        byte[] SentinelBody(int size)
        {
            var body = new byte[size];
            byte[] marker = Encoding.UTF8.GetBytes(BodySentinel);
            marker.AsSpan(0, Math.Min(marker.Length, size)).CopyTo(body);
            return body;
        }

        // The listener is started BEFORE any action below: Counter<T>.Add() calls are push-based and are
        // never replayed to a listener that subscribes later — only observable gauges are (via
        // RecordObservableInstruments(), polled once at the very end, after every action below). Catching
        // up on pre-existing instruments (the gauges and counters this adapter already created) happens
        // automatically the moment Start() runs, regardless of order relative to instrument creation.
        var longMeasurements = new List<KeyValuePair<string, object?>[]>();
        var intMeasurements = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name.StartsWith("barewire.inmemory.", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => longMeasurements.Add(tags.ToArray()));
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) => intMeasurements.Add(tags.ToArray()));
        listener.Start();

        // oversized (exchange declared -> tagged with "ex")
        var oversized = new OutboundMessage(RoutingKeySentinel, SentinelHeaders("ex"), SentinelBody(17), "");
        (await adapter.SendBatchAsync([oversized], ct)).Single().IsConfirmed.Should().BeFalse();

        // undeclared_exchange (never becomes the metric's exchange tag or a throttle key)
        var undeclaredExchange = new OutboundMessage(
            RoutingKeySentinel, SentinelHeaders(UndeclaredExchangeSentinel), SentinelBody(8), "");
        (await adapter.SendBatchAsync([undeclaredExchange], ct)).Single().IsConfirmed.Should().BeFalse();

        // unroutable (declared exchange, no matching binding for the sentinel routing key)
        var unroutable = new OutboundMessage(RoutingKeySentinel, SentinelHeaders("ex"), SentinelBody(8), "");
        await adapter.SendBatchAsync([unroutable], ct); // GuaranteedRouting is off -> still confirmed

        // queue_full: fill "qFull" (capacity 1) with an ordinary message, then reject the sentinel one —
        // also opens this queue's latch episode (full, no consumer), covering the health/latch logs below.
        var filler = new OutboundMessage("qFull", new Dictionary<string, string> { ["BW-Exchange"] = "" }, new byte[4], "");
        (await adapter.SendBatchAsync([filler], ct)).Single().IsConfirmed.Should().BeTrue();
        var queueFull = new OutboundMessage("qFull", SentinelHeaders(""), SentinelBody(4), "");
        (await adapter.SendBatchAsync([queueFull], ct)).Single().IsConfirmed.Should().BeFalse();

        // Latch is open: occupancy 1 of capacity 1 is >= 90% -> Warning 2303, polled a few times (logs
        // the transition at most once regardless of poll count).
        for (int i = 0; i < 3; i++)
        {
            adapter.GetHealth();
        }

        // Release "qFull"'s only slot properly (consume + Ack, not a direct slot release) — clears the
        // latch (Information 2302) and drops occupancy to 0 of 1, below the 80% recovery threshold
        // (Information 2304), polled a few times same as above.
        await using (IAsyncEnumerator<InboundMessage> e =
            adapter.ConsumeAsync("qFull", new FlowControlOptions(), ct).GetAsyncEnumerator(ct))
        {
            (await e.MoveNextAsync().AsTask().WaitAsync(Timeout, ct)).Should().BeTrue();
            await adapter.SettleAsync(SettlementAction.Ack, e.Current, ct);
        }

        for (int i = 0; i < 3; i++)
        {
            adapter.GetHealth();
        }

        // closed: the adapter itself is disposed, so every later send (and any message of a call already
        // in flight) is reported "closed" instead of throwing — reason only, never a queue or exchange.
        adapter.Dispose();
        var afterClose = new OutboundMessage(RoutingKeySentinel, SentinelHeaders("ex"), SentinelBody(4), "");
        (await adapter.SendBatchAsync([afterClose], ct)).Single().IsConfirmed.Should().BeFalse();

        // ── Assert: every barewire.inmemory.* measurement obeys the cardinality rule ──────────────────

        // Polls the three observable gauges now, with the adapter's final (post-dispose) state — every
        // push-based counter measurement was already captured live, above, as each action ran.
        listener.RecordObservableInstruments();

        IEnumerable<KeyValuePair<string, object?>[]> allMeasurements = longMeasurements.Concat(intMeasurements);
        allMeasurements.Should().NotBeEmpty();

        // Positive evidence that every rejection path above actually reached the counter — otherwise the
        // "no sentinel in any tag" assertions below would vacuously pass on an empty or partial set.
        List<string?> reasonsRecorded = longMeasurements
            .Select(tags => tags.FirstOrDefault(t => t.Key == "reason").Value as string)
            .Where(reason => reason is not null)
            .ToList();
        reasonsRecorded.Should().Contain(["oversized", "undeclared_exchange", "unroutable", "queue_full", "closed"]);

        foreach (KeyValuePair<string, object?>[] tags in allMeasurements)
        {
            tags.Select(t => t.Key).Should().BeSubsetOf(["queue", "exchange", "reason"]);
            tags.Count(t => t.Key is "queue" or "exchange").Should().BeLessThanOrEqualTo(1);

            foreach (KeyValuePair<string, object?> tag in tags)
            {
                string? value = tag.Value as string;
                if (value is not null)
                {
                    sentinels.Should().NotContain(value, "a tag value must never be a publisher-controlled sentinel");
                    foreach (string sentinel in sentinels)
                    {
                        value.Should().NotContain(sentinel, "a tag value must never embed a publisher-controlled sentinel");
                    }
                }
            }
        }

        // Positive evidence that the latch/health logs above actually fired — otherwise the "never
        // carries a sentinel" log check below would vacuously pass on an empty log.
        logger.Entries.Should().Contain(e => e.EventId.Id == 2301); // latch set (Warning)
        logger.Entries.Should().Contain(e => e.EventId.Id == 2302); // latch cleared (Information)
        logger.Entries.Should().Contain(e => e.EventId.Id == 2303); // queue degraded (Warning)
        logger.Entries.Should().Contain(e => e.EventId.Id == 2304); // queue recovered (Information)

        // ── Assert: no log entry ever carries the MessageId, other-header, or body sentinel ────────────
        // (the routing key and the already-declared "ex" exchange name ARE expected in some log entries —
        // see this test's own remarks — so neither is checked here.)

        string[] neverLogged = [MessageIdSentinel, OtherHeaderSentinel, BodySentinel];
        foreach (LogEntry entry in logger.Entries)
        {
            foreach (string sentinel in neverLogged)
            {
                entry.Message.Should().NotContain(sentinel);
            }

            foreach (KeyValuePair<string, object?> field in entry.State)
            {
                if (field.Value is string stringValue)
                {
                    foreach (string sentinel in neverLogged)
                    {
                        stringValue.Should().NotContain(sentinel);
                    }
                }
            }
        }

        // Note: the undeclared exchange name is NOT asserted absent from logs here. It is already proven
        // absent from every TAG above (the cardinality-rule loop, using the same `sentinels` array); the
        // log text is a separate contract — InMemorySendDiagnostics.MessageRejected deliberately falls
        // back to the publisher-supplied exchange name for its Error log's metadata when no declared name
        // is available (see that method's own remarks on `publisherExchange`), the same way it already
        // logs the routing key. Only the MessageId, other-header, and body sentinels — never legitimate
        // routing/exchange metadata — are excluded from every log entry, as asserted just above.

        // ── Assert: health endpoint descriptions never carry any sentinel ───────────────────────────────

        BusHealthStatus health = adapter.GetHealth();
        health.Description.Should().NotContainAny(sentinels);
        foreach (EndpointHealthStatus endpoint in health.Endpoints)
        {
            endpoint.Description.Should().NotBeNull();
            endpoint.Description!.Should().NotContainAny(sentinels);
        }
    }

    // ── Guards: a throwing metrics listener or logging provider never crashes the caller ─────────────

    [Fact]
    public void DeferredRedeliveryFailed_WhenLoggerThrows_DoesNotThrowAndIncrementsLogFailureCount()
    {
        var diagnostics = new InMemoryConsumeDiagnostics(new ThrowingLogger<InMemoryConsumeDiagnostics>());

        Action act = () => diagnostics.DeferredRedeliveryFailed("q", new InvalidOperationException("boom"));

        act.Should().NotThrow();
        diagnostics.LogFailureCount.Should().Be(1);
    }

    [Fact]
    public void SettlementDropped_WhenLoggerThrows_DoesNotThrowAndIncrementsLogFailureCount()
    {
        var diagnostics = new InMemoryConsumeDiagnostics(new ThrowingLogger<InMemoryConsumeDiagnostics>());

        Action act = () => diagnostics.SettlementDropped("q", SettlementDropReason.DeadLetterQueueFull);

        act.Should().NotThrow();
        diagnostics.LogFailureCount.Should().Be(1);
    }

    [Fact]
    public async Task SendBatchAsync_WhenUnroutableAndLoggerThrows_DoesNotSurfaceAsInternalError()
    {
        using var meter = new Meter("test-" + Guid.NewGuid());
        var rejected = ListenRejected(meter);
        InMemoryTransportAdapter a = Adapter(
            c => c.ConfigureTopology(t => t.DeclareExchange("ex", ExchangeType.Direct)),
            meter,
            new ThrowingLogger<InMemoryTransportAdapter>());

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToExchange("ex", "nowhere", "1")], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeTrue(); // GuaranteedRouting is off by default => still confirmed
        rejected.Should().ContainSingle(x => x.Reason == "unroutable");
        rejected.Should().NotContain(x => x.Reason == "internal_error");
    }

    [Fact]
    public async Task SendBatchAsync_WhenMeterListenerThrowsOnQueueFull_DoesNotSurfaceAsInternalError()
    {
        using var meter = new Meter("test-" + Guid.NewGuid());
        using ThrowingRejectedListener listener = new(meter, throwOnReason: "queue_full");
        InMemoryTransportAdapter a = Adapter(c =>
        {
            c.QueueCapacity(1);
            c.ConfigureTopology(t =>
            {
                t.DeclareExchange("f", ExchangeType.Fanout);
                t.DeclareQueue("q1");
                t.DeclareQueue("q2");
                t.BindExchangeToQueue("f", "q1", "");
                t.BindExchangeToQueue("f", "q2", "");
            });
        }, meter);
        Fill(Queue(a, "q2"));
        Queue(a, "q2").TryReserve().Should().Be(QueueReservationResult.Latched);

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToExchange("f", "", "m")], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeFalse();
        listener.Reasons.Should().ContainSingle(x => x == "queue_full");
        listener.Reasons.Should().NotContain("internal_error");
    }

    [Fact]
    public async Task SendBatchAsync_WhenMeterListenerThrowsOnOversized_DoesNotSurfaceAsInternalError()
    {
        using var meter = new Meter("test-" + Guid.NewGuid());
        using ThrowingRejectedListener listener = new(meter, throwOnReason: "oversized");
        InMemoryTransportAdapter a = Adapter(c =>
        {
            c.MaxMessageSize(16);
            c.ConfigureTopology(t => t.DeclareExchange("ex", ExchangeType.Direct));
        }, meter);

        IReadOnlyList<SendResult> r = await a.SendBatchAsync(
            [ToExchange("ex", "k", "big", size: 64)], TestContext.Current.CancellationToken);

        r.Single().IsConfirmed.Should().BeFalse();
        listener.Reasons.Should().ContainSingle(x => x == "oversized");
        listener.Reasons.Should().NotContain("internal_error");
    }

    /// <summary>
    /// Records the <c>reason</c> of every rejected-counter measurement on one meter, then throws for the
    /// configured reason — simulating a faulty metrics listener on the synchronous send path.
    /// </summary>
    private sealed class ThrowingRejectedListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Lock _gate = new();
        private readonly List<string> _reasons = [];

        internal ThrowingRejectedListener(Meter meter, string throwOnReason)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter)
                    && instrument.Name == InMemoryTransportMetrics.RejectedCounterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                string reason = string.Empty;
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    if (tag.Key == InMemoryTransportMetrics.ReasonTag)
                    {
                        reason = tag.Value as string ?? string.Empty;
                    }
                }

                lock (_gate)
                {
                    _reasons.Add(reason);
                }

                if (reason == throwOnReason)
                {
                    throw new InvalidOperationException("listener failure");
                }
            });
            _listener.Start();
        }

        internal IReadOnlyList<string> Reasons
        {
            get
            {
                lock (_gate)
                {
                    return [.. _reasons];
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
