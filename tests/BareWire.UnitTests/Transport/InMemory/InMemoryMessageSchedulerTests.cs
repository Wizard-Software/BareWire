using System.Buffers;
using System.Globalization;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
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
/// Covers <see cref="InMemoryMessageScheduler"/> — the mechanism behind
/// <see cref="InMemoryTransportAdapter"/>'s <see cref="INativeMessageScheduler"/> capability — exercised
/// entirely through the adapter's public <see cref="INativeMessageScheduler.ScheduleAsync"/> and
/// <see cref="INativeMessageScheduler.CancelScheduledAsync"/> surface plus its internal test hooks
/// (<see cref="InMemoryTransportAdapter.PendingScheduledCount"/>,
/// <see cref="InMemoryTransportAdapter.ScheduledDeliveryFailedCount"/>).
/// </summary>
public sealed class InMemoryMessageSchedulerTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────────────

    private static InMemoryTransportAdapter Adapter(
        Action<IInMemoryConfigurator> configure,
        CountingBufferPoolObserver? observer = null,
        TimeProvider? timeProvider = null,
        ILogger<InMemoryTransportAdapter>? logger = null,
        int maxPendingScheduled = InMemoryMessageScheduler.DefaultMaxPending)
    {
        var c = new InMemoryConfigurator();
        configure(c);
        InMemoryTransportOptions o = c.Build();
        return new InMemoryTransportAdapter(
            o, new InMemoryBroker(o), logger: logger, timeProvider: timeProvider, bufferPoolObserver: observer,
            maxPendingScheduled: maxPendingScheduled);
    }

    private static InMemoryTransportAdapter CreateAdapter(
        TimeProvider timeProvider, string queue, CountingBufferPoolObserver? observer = null,
        int maxPendingScheduled = InMemoryMessageScheduler.DefaultMaxPending) =>
        Adapter(c => c.ConfigureTopology(t => t.DeclareQueue(queue)), observer, timeProvider,
            maxPendingScheduled: maxPendingScheduled);

    private static InMemoryQueue Queue(InMemoryTransportAdapter adapter, string name)
    {
        adapter.Broker.TryGetQueue(name, out InMemoryQueue? queue).Should().BeTrue();
        return queue!;
    }

    private static OutboundMessage Message(
        string routingKey, byte[] body, IReadOnlyDictionary<string, string>? headers = null, string contentType = "") =>
        new(routingKey, headers ?? new Dictionary<string, string>(), body, contentType);

    private static async Task<InboundMessage> ConsumeOneAsync(
        InMemoryTransportAdapter adapter, string queueName, TimeSpan timeout)
    {
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(queueName, new FlowControlOptions(), TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(timeout, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        return enumerator.Current;
    }

    // ILogger<InMemoryTransportAdapter> that captures the value of an AsyncLocal at the moment each log
    // call reaches it — used to prove the timer callback's ExecutionContext was suppressed at creation
    // time, not merely that nothing was ever logged.
    private sealed class AsyncLocalCapturingLogger(AsyncLocal<string?> local) : ILogger<InMemoryTransportAdapter>
    {
        internal int LogCount { get; private set; }

        internal string? ObservedDuringLastLog { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ObservedDuringLastLog = local.Value;
            LogCount++;
        }
    }

    // ── Delivery timing ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleAsync_WhenDueTimeReached_DeliversToDestinationQueue()
    {
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "orders");
        OutboundMessage message = Message(routingKey: "orders", body: "timeout"u8.ToArray());

        ScheduledMessageToken token = await adapter.ScheduleAsync(
            message, fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken);

        token.Destination.Should().Be("orders");
        adapter.PendingScheduledCount.Should().Be(1);
        fake.Advance(TimeSpan.FromMinutes(5));

        InboundMessage delivered = await ConsumeOneAsync(adapter, "orders", TimeSpan.FromSeconds(5));
        delivered.Body.ToArray().Should().Equal("timeout"u8.ToArray());
        adapter.PendingScheduledCount.Should().Be(0);
    }

    [Fact]
    public async Task ScheduleAsync_BeforeDueTime_DoesNotDeliver()
    {
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "orders");
        OutboundMessage message = Message("orders", "timeout"u8.ToArray());

        await adapter.ScheduleAsync(message, fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken);
        fake.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));

        // Asserted via queue occupancy and the pending count, never via a short-timeout read — with
        // FakeTimeProvider a due timer fires synchronously inside Advance, so nothing here is a race.
        Queue(adapter, "orders").Occupancy.Should().Be(0);
        adapter.PendingScheduledCount.Should().Be(1);
    }

    [Fact]
    public async Task ScheduleAsync_TimeInPast_DeliversImmediately()
    {
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "orders");
        OutboundMessage message = Message("orders", "timeout"u8.ToArray());

        await adapter.ScheduleAsync(message, fake.GetUtcNow().AddMinutes(-1), TestContext.Current.CancellationToken);

        InboundMessage delivered = await ConsumeOneAsync(adapter, "orders", TimeSpan.FromSeconds(5));
        delivered.Body.ToArray().Should().Equal("timeout"u8.ToArray());
        adapter.PendingScheduledCount.Should().Be(0);
    }

    [Fact]
    public async Task ScheduleAsync_CallerMutatesBodyAfterSchedule_DeliversOriginalBytes()
    {
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "orders");
        byte[] body = "original"u8.ToArray();
        OutboundMessage message = Message("orders", body);

        await adapter.ScheduleAsync(message, fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken);
        Array.Clear(body);
        fake.Advance(TimeSpan.FromMinutes(5));

        InboundMessage delivered = await ConsumeOneAsync(adapter, "orders", TimeSpan.FromSeconds(5));
        delivered.Body.ToArray().Should().Equal("original"u8.ToArray());
    }

    // ── Cancellation ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelScheduledAsync_BeforeDueTime_PreventsDeliveryAndReturnsBuffer()
    {
        var observer = new CountingBufferPoolObserver();
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "orders", observer);
        OutboundMessage message = Message("orders", "timeout"u8.ToArray());

        ScheduledMessageToken token = await adapter.ScheduleAsync(
            message, fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken);
        await adapter.CancelScheduledAsync(token, TestContext.Current.CancellationToken);
        fake.Advance(TimeSpan.FromMinutes(10));

        Queue(adapter, "orders").Occupancy.Should().Be(0);
        adapter.PendingScheduledCount.Should().Be(0);
        observer.Rented.Should().Be(observer.Returned);
        observer.Violations.Should().BeEmpty();

        Func<Task> secondCancel = () => adapter.CancelScheduledAsync(token, TestContext.Current.CancellationToken);
        await secondCancel.Should().NotThrowAsync();
    }

    // ── Validation before ownership ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleAsync_UnknownDestination_ThrowsTransportExceptionWithoutRentingBuffer()
    {
        var observer = new CountingBufferPoolObserver();
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "orders", observer);
        OutboundMessage message = Message("missing", "timeout"u8.ToArray());

        Func<Task> act = () => adapter.ScheduleAsync(
            message, fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<BareWireTransportException>()).Which.Message.Should().Contain("missing");
        observer.Rented.Should().Be(0);
        adapter.PendingScheduledCount.Should().Be(0);
    }

    [Fact]
    public async Task ScheduleAsync_DelayBeyondTimerLimit_ThrowsArgumentOutOfRange()
    {
        var observer = new CountingBufferPoolObserver();
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "orders", observer);
        OutboundMessage message = Message("orders", "timeout"u8.ToArray());
        DateTimeOffset tooFar = fake.GetUtcNow() + InMemoryTransportOptions.MaxDeferDelay + TimeSpan.FromMilliseconds(1);

        Func<Task> act = () => adapter.ScheduleAsync(message, tooFar, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        observer.Rented.Should().Be(0);
        adapter.PendingScheduledCount.Should().Be(0);
    }

    [Fact]
    public async Task ScheduleAsync_BodyExceedsMaxMessageSize_ThrowsWithoutRentingBuffer()
    {
        var observer = new CountingBufferPoolObserver();
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.MaxMessageSize(8);
                c.ConfigureTopology(t => t.DeclareQueue("orders"));
            },
            observer, fake);
        OutboundMessage message = Message("orders", new byte[9]);

        Func<Task> act = () => adapter.ScheduleAsync(
            message, fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BareWireTransportException>();
        observer.Rented.Should().Be(0);
        adapter.PendingScheduledCount.Should().Be(0);
    }

    [Fact]
    public async Task ScheduleAsync_WhenPendingLimitReached_ThrowsTransportException()
    {
        var observer = new CountingBufferPoolObserver();
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "orders", observer, maxPendingScheduled: 2);

        await adapter.ScheduleAsync(
            Message("orders", "m1"u8.ToArray()), fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken);
        await adapter.ScheduleAsync(
            Message("orders", "m2"u8.ToArray()), fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken);
        int rentedAfterTwo = observer.Rented;

        Func<Task> act = () => adapter.ScheduleAsync(
            Message("orders", "m3"u8.ToArray()), fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BareWireTransportException>();
        adapter.PendingScheduledCount.Should().Be(2);
        observer.Rented.Should().Be(rentedAfterTwo);
    }

    [Fact]
    public async Task ScheduleAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var fake = new FakeTimeProvider(Epoch);
        InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "orders");
        adapter.Dispose();

        Func<Task> act = () => adapter.ScheduleAsync(
            Message("orders", "m1"u8.ToArray()), fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // ── Routing ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleAsync_WithExchangeHeader_RoutesThroughExchange()
    {
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("orders-exchange", ExchangeType.Direct);
            t.DeclareQueue("orders");
            t.BindExchangeToQueue("orders-exchange", "orders", "order.created");
        }), timeProvider: fake);

        OutboundMessage message = Message(
            "order.created", "timeout"u8.ToArray(),
            headers: new Dictionary<string, string> { ["BW-Exchange"] = "orders-exchange" });

        await adapter.ScheduleAsync(message, fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken);
        fake.Advance(TimeSpan.FromMinutes(5));

        InboundMessage delivered = await ConsumeOneAsync(adapter, "orders", TimeSpan.FromSeconds(5));
        delivered.Body.ToArray().Should().Equal("timeout"u8.ToArray());
    }

    [Fact]
    public async Task ScheduleAsync_WithLowercaseExchangeHeaderVariant_UsesDefaultExchangeNotTheLowercaseValue()
    {
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t =>
        {
            t.DeclareQueue("orders");
            t.DeclareExchange("evil-exchange", ExchangeType.Direct);
            t.DeclareQueue("evil");
            t.BindExchangeToQueue("evil-exchange", "evil", "orders");
        }), timeProvider: fake);

        // Only a lowercase "bw-exchange" variant is supplied — never the canonical "BW-Exchange" header.
        // The exact-case lookup Schedule (validation) and the fire path (re-send through InMemorySender)
        // both use must never honor it: the message resolves against the DEFAULT exchange (direct to the
        // "orders" queue by name), never against "evil-exchange" — which, if the lowercase variant leaked
        // through, would redirect it to the "evil" queue instead.
        OutboundMessage message = Message(
            "orders", "timeout"u8.ToArray(),
            headers: new Dictionary<string, string> { ["bw-exchange"] = "evil-exchange" });

        await adapter.ScheduleAsync(message, fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken);
        fake.Advance(TimeSpan.FromMinutes(5));

        Queue(adapter, "orders").Occupancy.Should().Be(1);
        Queue(adapter, "evil").Occupancy.Should().Be(0);
    }

    // ── Dispose ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_WithPendingScheduledMessages_ReturnsEveryBufferExactlyOnce()
    {
        var observer = new CountingBufferPoolObserver();
        var fake = new FakeTimeProvider(Epoch);
        InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "orders", observer);

        await adapter.ScheduleAsync(
            Message("orders", "m1"u8.ToArray()), fake.GetUtcNow().AddMinutes(10), TestContext.Current.CancellationToken);
        await adapter.ScheduleAsync(
            Message("orders", "m2"u8.ToArray()), fake.GetUtcNow().AddMinutes(20), TestContext.Current.CancellationToken);
        await adapter.ScheduleAsync(
            Message("orders", "m3"u8.ToArray()), fake.GetUtcNow().AddMinutes(30), TestContext.Current.CancellationToken);

        Action act = () =>
        {
            adapter.Dispose();
            fake.Advance(TimeSpan.FromHours(1)); // the cancelled timers write nothing back
        };

        act.Should().NotThrow();
        adapter.PendingScheduledCount.Should().Be(0);
        observer.Rented.Should().Be(observer.Returned);
        observer.Violations.Should().BeEmpty();
    }

    // ── Fire-path capacity, retries, and context isolation ────────────────────────────────────────

    [Fact]
    public async Task ScheduledDelivery_WhenQueueFull_IsRejectedLikeSendAndReturnsBuffer()
    {
        var observer = new CountingBufferPoolObserver();
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.QueueCapacity(1);
                c.SendTimeout(TimeSpan.Zero); // deterministic: a full queue is rejected immediately, no wait
                c.ConfigureTopology(t => t.DeclareQueue("orders"));
            },
            observer, fake);

        IReadOnlyList<SendResult> filling = await adapter.SendBatchAsync(
            [Message("orders", "filler"u8.ToArray(), new Dictionary<string, string> { ["BW-Exchange"] = "" })],
            TestContext.Current.CancellationToken);
        filling.Should().ContainSingle(r => r.IsConfirmed);

        await adapter.ScheduleAsync(
            Message("orders", "timeout"u8.ToArray()), fake.GetUtcNow().AddMinutes(1), TestContext.Current.CancellationToken);
        fake.Advance(TimeSpan.FromMinutes(1));

        adapter.ScheduledDeliveryFailedCount.Should().Be(1);
        observer.Violations.Should().BeEmpty();
        // Only the still-queued filler message's buffer remains outstanding — the scheduler's own buffer
        // for the rejected fire was returned exactly once.
        (observer.Rented - observer.Returned).Should().Be(1);
    }

    [Fact]
    public async Task ScheduleAsync_AsyncLocalSetBeforeCall_DoesNotFlowIntoFirePathLogging()
    {
        var local = new AsyncLocal<string?> { Value = "leaked-if-flowed" };
        var logger = new AsyncLocalCapturingLogger(local);
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.QueueCapacity(1);
                c.SendTimeout(TimeSpan.Zero);
                c.ConfigureTopology(t => t.DeclareQueue("orders"));
            },
            timeProvider: fake, logger: logger);

        await adapter.SendBatchAsync(
            [Message("orders", "filler"u8.ToArray(), new Dictionary<string, string> { ["BW-Exchange"] = "" })],
            TestContext.Current.CancellationToken);

        await adapter.ScheduleAsync(
            Message("orders", "timeout"u8.ToArray()), fake.GetUtcNow().AddMinutes(1), TestContext.Current.CancellationToken);

        // Cleared AFTER the timer was created (so "leaked-if-flowed" is only ever available as a CAPTURED
        // ExecutionContext snapshot from creation time, never as this thread's current ambient value) and
        // BEFORE Advance fires the callback synchronously on this same thread — if ExecutionContext.SuppressFlow()
        // at timer-creation time did not work, the callback would still observe the stale "leaked-if-flowed"
        // snapshot instead of this thread's current (cleared) value.
        local.Value = null;
        fake.Advance(TimeSpan.FromMinutes(1)); // fires against the still-full queue, forcing a log call

        // Positive evidence the fire path actually ran and logged — not merely "never observed anything".
        logger.LogCount.Should().BeGreaterThan(0);
        logger.ObservedDuringLastLog.Should().BeNull();
    }

    // ── Guarded logging (a throwing provider must never crash the timer thread, fault the fire-and- ──
    // ── forget task, or abort Dispose before its own sweep) ───────────────────────────────────────────

    // ILogger<InMemoryTransportAdapter> whose Log always throws — proves the catch-and-count guard added
    // to every log call in InMemoryMessageScheduler. Mirrors InMemoryQueueLatchEpisodeTests's own
    // ThrowingLogger (private per test class, since it is not shared across files).
    private sealed class ThrowingLogger : ILogger<InMemoryTransportAdapter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger provider boom");
    }

    [Fact]
    public async Task ScheduledDelivery_WhenLoggerThrows_TimerCallbackDoesNotThrowAndIncrementsLogFailureCount()
    {
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.QueueCapacity(1);
                c.SendTimeout(TimeSpan.Zero); // deterministic: a full queue is rejected immediately, no wait
                c.ConfigureTopology(t => t.DeclareQueue("orders"));
            },
            timeProvider: fake, logger: new ThrowingLogger());

        await adapter.SendBatchAsync(
            [Message("orders", "filler"u8.ToArray(), new Dictionary<string, string> { ["BW-Exchange"] = "" })],
            TestContext.Current.CancellationToken);
        await adapter.ScheduleAsync(
            Message("orders", "timeout"u8.ToArray()), fake.GetUtcNow().AddMinutes(1), TestContext.Current.CancellationToken);

        // The timer fires synchronously inside Advance, on this thread; FireAsync's ReportDeliveryFailed
        // call below is what exercises the throwing logger — this must never escape as an exception here.
        Action act = () => fake.Advance(TimeSpan.FromMinutes(1));

        act.Should().NotThrow();
        adapter.ScheduledDeliveryFailedCount.Should().Be(1);
        adapter.ScheduledDeliveryLogFailureCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Dispose_WithPendingScheduledMessagesAndLoggerThrows_DoesNotThrowAndIncrementsLogFailureCount()
    {
        var fake = new FakeTimeProvider(Epoch);
        InMemoryTransportAdapter adapter = Adapter(
            c => c.ConfigureTopology(t => t.DeclareQueue("orders")),
            timeProvider: fake, logger: new ThrowingLogger());

        await adapter.ScheduleAsync(
            Message("orders", "m1"u8.ToArray()), fake.GetUtcNow().AddMinutes(10), TestContext.Current.CancellationToken);
        await adapter.ScheduleAsync(
            Message("orders", "m2"u8.ToArray()), fake.GetUtcNow().AddMinutes(20), TestContext.Current.CancellationToken);

        // Dispose's own shutdown-drop report throws through the same guarded path; the adapter's InFlight
        // sweep after this call must still run, which it can only do if Dispose itself never throws.
        Action act = () => adapter.Dispose();

        act.Should().NotThrow();
        adapter.ScheduledDeliveryLogFailureCount.Should().BeGreaterThan(0);
        adapter.PendingScheduledCount.Should().Be(0);
    }

    // ── Throttle keyed by the validated exchange, never the publisher-supplied routing key ───────────

    [Fact]
    public async Task ScheduledDelivery_ManyDistinctRoutingKeysThroughSameExchange_LogsOnceNotOncePerRoutingKey()
    {
        var logger = new CapturingLogger<InMemoryTransportAdapter>();
        var fake = new FakeTimeProvider(Epoch);
        var headers = new Dictionary<string, string> { ["BW-Exchange"] = "orders-exchange" };
        using InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.QueueCapacity(1);
                c.SendTimeout(TimeSpan.Zero); // deterministic: a full queue is rejected immediately, no wait
                c.ConfigureTopology(t =>
                {
                    t.DeclareExchange("orders-exchange", ExchangeType.Topic);
                    t.DeclareQueue("orders");
                    t.BindExchangeToQueue("orders-exchange", "orders", "order.*");
                });
            },
            logger: logger, timeProvider: fake);

        // Fills the single slot so every scheduled fire below finds the queue full.
        await adapter.SendBatchAsync([Message("order.filler", "filler"u8.ToArray(), headers)], TestContext.Current.CancellationToken);

        const int distinctRoutingKeys = 25;
        for (int i = 0; i < distinctRoutingKeys; i++)
        {
            await adapter.ScheduleAsync(
                Message($"order.{i}", "timeout"u8.ToArray(), headers), fake.GetUtcNow().AddMinutes(1),
                TestContext.Current.CancellationToken);
        }

        // Every one of the 25 timers due at the same instant fires synchronously inside this single
        // Advance call, all within the same 60-second throttle window.
        fake.Advance(TimeSpan.FromMinutes(1));

        adapter.ScheduledDeliveryFailedCount.Should().Be(distinctRoutingKeys);

        // Before the fix, each of the 25 distinct routing keys got its own throttle-state entry (keyed by
        // the publisher-controlled Destination) and therefore its own first-occurrence Warning. Keyed by
        // the bounded, validated exchange instead, all 25 share one throttle state — exactly one entry.
        IReadOnlyList<LogEntry> deliveryFailedEntries =
            [.. logger.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("was not confirmed"))];
        deliveryFailedEntries.Should().ContainSingle();
        deliveryFailedEntries[0]["Exchange"].Should().Be("orders-exchange");
    }

    // Note: a test proving the pending-count reservation stays held while a fire is genuinely still
    // in-flight (awaiting queue capacity) was attempted here and dropped — a queue with no active consumer
    // latches and rejects immediately without ever waiting (see the README's "Publisher isolation when a
    // consumer stalls" section), so reaching a real, observable in-flight await deterministically would
    // require driving an active-but-non-draining consumer, which is not practical to set up reliably from
    // this test surface. FireAsync's finally-block decrement (see the source) and the buffer-accounting
    // parity already asserted by Dispose_WithPendingScheduledMessages_ReturnsEveryBufferExactlyOnce and
    // ScheduledDelivery_WhenQueueFull_IsRejectedLikeSendAndReturnsBuffer above remain the coverage for this
    // change.

    // ── ExecutionContext.SuppressFlow() guard against an already-suppressed caller ────────────────────

    [Fact]
    public async Task ScheduleAsync_WhenExecutionContextFlowAlreadySuppressed_DoesNotThrow()
    {
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "orders");
        OutboundMessage message = Message("orders", "timeout"u8.ToArray());

        Action act = () =>
        {
            using (ExecutionContext.SuppressFlow())
            {
                // A caller running inside its own SuppressFlow scope: Schedule's internal
                // ExecutionContext.SuppressFlow() would throw InvalidOperationException without the
                // IsFlowSuppressed() guard.
                adapter.ScheduleAsync(message, fake.GetUtcNow().AddMinutes(5), TestContext.Current.CancellationToken)
                    .GetAwaiter().GetResult();
            }
        };

        act.Should().NotThrow();
        adapter.PendingScheduledCount.Should().Be(1);

        fake.Advance(TimeSpan.FromMinutes(5));
        InboundMessage delivered = await ConsumeOneAsync(adapter, "orders", TimeSpan.FromSeconds(5));
        delivered.Body.ToArray().Should().Equal("timeout"u8.ToArray());
    }
}
