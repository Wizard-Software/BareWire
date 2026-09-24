using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

/// <summary>
/// Covers latch episodes end to end: <see cref="InMemoryQueue"/>'s winner-detection of the 0→1 and 1→0
/// latch transitions (including the "last observer wins" replacement rule and the ABA-guarding revert
/// branch), and <see cref="InMemoryQueueDiagnostics"/>'s aggregation of an entire episode — no matter how
/// many rejected copies it covers — into exactly one throttled <see cref="LogLevel.Warning"/> plus, only
/// for an episode whose warning was actually logged, exactly one <see cref="LogLevel.Information"/>.
/// </summary>
public sealed class InMemoryQueueLatchEpisodeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static InMemoryDelivery Delivery() => new(new byte[4], 4);

    // ── Adapter-level fixtures (mirrors InMemoryTransportAdapterSendTests / InMemoryTransportMetricsTests) ──

    private static InMemoryTransportAdapter Adapter(
        Action<IInMemoryConfigurator> configure,
        CountingBufferPoolObserver? bufferPoolObserver = null,
        Meter? meter = null,
        ILogger<InMemoryTransportAdapter>? logger = null,
        TimeProvider? timeProvider = null)
    {
        var c = new InMemoryConfigurator();
        configure(c);
        InMemoryTransportOptions o = c.Build();
        return new InMemoryTransportAdapter(
            o, new InMemoryBroker(o), logger: logger, meter: meter, timeProvider: timeProvider,
            bufferPoolObserver: bufferPoolObserver);
    }

    // message to the default exchange ("" via BW-Exchange) routed straight to queue `queue`
    private static OutboundMessage ToQueue(string queue, string id, int size = 8) =>
        new(queue, new Dictionary<string, string> { ["BW-Exchange"] = "", ["message-id"] = id }, new byte[size], "");

    private static async Task<(InboundMessage Message, IAsyncEnumerator<InboundMessage> Enumerator)> ReceiveOneAsync(
        InMemoryTransportAdapter adapter, string queueName, CancellationToken ct)
    {
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(queueName, new FlowControlOptions(), ct).GetAsyncEnumerator(ct);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout, ct)).Should().BeTrue();
        return (enumerator.Current, enumerator);
    }

    // Consumes and Acks `count` deliveries from `queueName`, releasing their slots one by one.
    private static async Task DrainAndAckAsync(
        InMemoryTransportAdapter adapter, string queueName, int count, CancellationToken ct)
    {
        IAsyncEnumerator<InboundMessage> e =
            adapter.ConsumeAsync(queueName, new FlowControlOptions(), ct).GetAsyncEnumerator(ct);
        for (int i = 0; i < count; i++)
        {
            (await e.MoveNextAsync().AsTask().WaitAsync(Timeout, ct)).Should().BeTrue();
            await adapter.SettleAsync(SettlementAction.Ack, e.Current, ct);
        }

        await e.DisposeAsync();
    }

    // ILogger<InMemoryTransportAdapter> whose Log always throws — proves the F3 catch-and-count guard.
    private sealed class ThrowingLogger : ILogger<InMemoryTransportAdapter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger provider boom");
    }

    // Records every latch-set/latch-cleared notification it receives, for direct InMemoryQueue-level tests.
    private sealed class RecordingLatchObserver : IInMemoryQueueLatchObserver
    {
        private int _setCount;
        private int _clearedCount;

        internal int SetCount => Volatile.Read(ref _setCount);

        internal int ClearedCount => Volatile.Read(ref _clearedCount);

        public long GetTimestamp() => 0;

        public void OnLatchSet(InMemoryQueue queue, InMemoryLatchEpisode episode) => Interlocked.Increment(ref _setCount);

        public void OnLatchCleared(InMemoryQueue queue, InMemoryLatchEpisode episode) => Interlocked.Increment(ref _clearedCount);
    }

    // ── InMemoryQueue: winner detection, "last observer wins", concurrency, the ABA-guarding revert branch ──

    [Fact]
    public void TryReserve_WhenFullWithoutConsumerRepeatedly_NotifiesLatchSetExactlyOnce()
    {
        var observer = new RecordingLatchObserver();
        var queue = new InMemoryQueue("q", capacity: 10);
        queue.SetLatchObserver(observer);

        for (int i = 0; i < 10; i++)
        {
            queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
            queue.WriteReserved(Delivery());
        }

        for (int i = 0; i < 1000; i++)
        {
            queue.TryReserve().Should().Be(QueueReservationResult.Latched);
        }

        queue.TryLatch().Should().BeTrue(); // already latched — observes, does not re-open

        observer.SetCount.Should().Be(1);
        observer.ClearedCount.Should().Be(0);
    }

    [Fact]
    public void ReleaseSlot_WhenOccupancyDropsBelowHalf_NotifiesLatchClearedExactlyOnce()
    {
        var observer = new RecordingLatchObserver();
        var queue = new InMemoryQueue("q", capacity: 10);
        queue.SetLatchObserver(observer);
        for (int i = 0; i < 10; i++)
        {
            queue.TryReserve();
            queue.WriteReserved(Delivery());
        }

        queue.TryReserve().Should().Be(QueueReservationResult.Latched);
        observer.SetCount.Should().Be(1);

        for (int i = 0; i < 5; i++)
        {
            queue.ReleaseSlot().Should().BeFalse(); // occupancy 10 -> 5: 5 is not < 5 (50%) — still latched
        }

        observer.ClearedCount.Should().Be(0);

        queue.ReleaseSlot().Should().BeTrue(); // occupancy 4 < 5 -> latch released
        observer.ClearedCount.Should().Be(1);

        for (int i = 0; i < 3; i++)
        {
            queue.ReleaseSlot();
        }

        observer.ClearedCount.Should().Be(1); // further releases never notify again
    }

    [Fact]
    public void SetLatchObserver_WhenReplaced_OnlyLatestObserverIsNotified()
    {
        var first = new RecordingLatchObserver();
        var second = new RecordingLatchObserver();
        var queue = new InMemoryQueue("q", capacity: 2);
        queue.SetLatchObserver(first);
        queue.SetLatchObserver(second);

        queue.TryReserve();
        queue.WriteReserved(Delivery());
        queue.TryReserve();
        queue.WriteReserved(Delivery());
        queue.TryReserve().Should().Be(QueueReservationResult.Latched);

        first.SetCount.Should().Be(0);
        second.SetCount.Should().Be(1);
    }

    [Fact]
    public async Task LatchTransitions_UnderConcurrentReserveAndRelease_EverySetHasMatchingClear()
    {
        var observer = new RecordingLatchObserver();
        var queue = new InMemoryQueue("q", capacity: 4);
        queue.SetLatchObserver(observer);

        const int workers = 16;
        const int iterations = 2000;
        var tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            tasks[w] = Task.Run(() =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    // Reserve-then-immediately-release (never WriteReserved): a legal way to hold and
                    // give back a slot per this type's own contract, and the only one safe here — nothing
                    // ever consumes this queue's channel, so a paired WriteReserved would accumulate items
                    // in it forever even as ReleaseSlot keeps decrementing occupancy back down.
                    if (queue.TryReserve() == QueueReservationResult.Reserved)
                    {
                        Thread.Yield();
                        queue.ReleaseSlot();
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        queue.Occupancy.Should().Be(0); // every Reserved was paired with a ReleaseSlot in the same iteration
        queue.IsLatched.Should().BeFalse();
        observer.SetCount.Should().Be(observer.ClearedCount); // every open has exactly one matching close
    }

    [Fact]
    public void TryLatch_WhenOccupancyAlreadyBelowThreshold_RevertsWithoutOpeningOrLeakingAnEpisode()
    {
        var observer = new RecordingLatchObserver();
        var queue = new InMemoryQueue("q", capacity: 10);
        queue.SetLatchObserver(observer);
        queue.TryReserve(); // occupancy 1, well below the 50% release threshold
        queue.WriteReserved(Delivery());

        queue.TryLatch().Should().BeFalse(); // wins 0→1, immediately reverts 1→0 on its own re-check

        queue.IsLatched.Should().BeFalse();
        observer.SetCount.Should().Be(0);
        observer.ClearedCount.Should().Be(0); // nothing was open — the ABA-guard close is a no-op here
    }

    /// <summary>
    /// A best-effort stress test for the ABA guard mandated in <see cref="InMemoryQueue.SetLatchAndRecheck"/>:
    /// half the workers hammer <c>TryLatch</c> — which drives the revert branch constantly at this
    /// capacity, since occupancy sits right at the release threshold — while the other half hammer
    /// reserve/release cycles that flip the latch for real, both against the SAME tight (capacity 2)
    /// boundary for a bounded wall-clock duration, to maximize the chance of the exact interleaving the
    /// guard defends against. Verified by mutation (this project's rule-6 substitute recipe): with the
    /// revert branch's <c>TryCloseLatchEpisode()</c> call removed, this assertion did not reproduce a
    /// failure across 5 runs of this exact test — the race window this guard defends against (see that
    /// method's own remarks) appears narrower than random OS thread scheduling reliably hits under this
    /// harness. The guard is kept as specified (a one-line, provably-safe no-op whenever nothing is open —
    /// see <see cref="TryLatch_WhenOccupancyAlreadyBelowThreshold_RevertsWithoutOpeningOrLeakingAnEpisode"/>)
    /// and this test is kept as a standing regression check on the broader invariant it asserts
    /// (<c>SetCount == ClearedCount</c> under heavy contention), even though it did not end up
    /// demonstrating the guard's specific necessity.
    /// </summary>
    [Fact]
    public async Task LatchTransitions_UnderTightBoundaryContention_NeverLeavesEpisodeCountMismatched()
    {
        var observer = new RecordingLatchObserver();
        var queue = new InMemoryQueue("q", capacity: 2); // release threshold: occupancy < 1
        queue.SetLatchObserver(observer);
        queue.TryReserve().Should().Be(QueueReservationResult.Reserved); // occupancy 1

        // Deliberately small and short-lived: this project runs test classes in parallel, and a longer or
        // wider CPU-saturating busy-loop here starves the thread pool enough to time out unrelated,
        // timing-sensitive tests running concurrently elsewhere in the suite.
        const int latchers = 4;
        const int reservers = 4;
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var tasks = new List<Task>(latchers + reservers);

        for (int i = 0; i < latchers; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    queue.TryLatch();
                }
            }));
        }

        for (int i = 0; i < reservers; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    // Reserve-then-give-back, never WriteReserved — see the sibling concurrency test's
                    // remarks; nothing here ever consumes this queue's channel. Only ever release a
                    // reservation THIS iteration actually won — never a speculative release based on
                    // Occupancy alone, which would release a slot this thread never owned.
                    if (queue.TryReserve() == QueueReservationResult.Reserved)
                    {
                        queue.ReleaseSlot();
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        while (queue.Occupancy > 0)
        {
            queue.ReleaseSlot();
        }

        queue.IsLatched.Should().BeFalse();
        observer.SetCount.Should().Be(observer.ClearedCount);
    }

    // ── InMemoryQueueDiagnostics through the adapter: one Warning + one Information per episode ─────────

    [Fact]
    public async Task SendBatchAsync_When1000CopiesRejectedDuringOneLatch_LogsOneWarningAndOneInformation()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger<InMemoryTransportAdapter>();
        using var meter = new Meter("test-" + Guid.NewGuid());
        List<(string Queue, long Count)> latchEpisodes = [];
        InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.QueueCapacity(10);
                c.ConfigureTopology(t => t.DeclareQueue("q"));
            },
            meter: meter,
            logger: logger,
            timeProvider: time);
        using MeterListener listener = ListenLatchEpisodesInto(meter, latchEpisodes);

        OutboundMessage[] messages = [.. Enumerable.Range(0, 1010).Select(i => ToQueue("q", $"m{i}"))];
        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(messages, ct);
        results.Count(r => r.IsConfirmed).Should().Be(10);
        results.Count(r => !r.IsConfirmed).Should().Be(1000);

        time.Advance(TimeSpan.FromSeconds(5));
        await DrainAndAckAsync(adapter, "q", 6, ct); // occupancy 10 -> 4 < 5 -> latch released

        logger.Entries.Count(e => e.EventId.Id == 2301 && e.Level == LogLevel.Warning).Should().Be(1);
        logger.Entries.Count(e => e.EventId.Id == 2302 && e.Level == LogLevel.Information).Should().Be(1);
        LogEntry info = logger.Entries.Single(e => e.EventId.Id == 2302);
        info["RejectedCount"].Should().Be(1000L);
        info["DurationMs"].Should().Be(5000L);
        logger.Entries.Count(e => e.Level >= LogLevel.Warning).Should().Be(1); // nothing per message
        latchEpisodes.Sum(m => m.Count).Should().Be(1);
    }

    [Fact]
    public async Task SendBatchAsync_WhenLatchEpisodesRecurWithinWindow_ThrottlesWarningAndCarriesSuppressedCount()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger<InMemoryTransportAdapter>();
        using var meter = new Meter("test-" + Guid.NewGuid());
        List<(string Queue, long Count)> latchEpisodes = [];
        InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.QueueCapacity(2);
                c.ConfigureTopology(t => t.DeclareQueue("q"));
            },
            meter: meter,
            logger: logger,
            timeProvider: time);
        using MeterListener listener = ListenLatchEpisodesInto(meter, latchEpisodes);

        // Episode 1: opens and closes — its Warning is logged, so its Information is too.
        await adapter.SendBatchAsync([ToQueue("q", "1"), ToQueue("q", "2"), ToQueue("q", "3")], ct);
        await DrainAndAckAsync(adapter, "q", 2, ct);

        // Episode 2, same 60 s window: opens (counted) and closes, but its Warning is throttled away —
        // so its Information never logs either.
        await adapter.SendBatchAsync([ToQueue("q", "4"), ToQueue("q", "5"), ToQueue("q", "6")], ct);
        await DrainAndAckAsync(adapter, "q", 2, ct);

        logger.Entries.Count(e => e.EventId.Id == 2301).Should().Be(1);
        logger.Entries.Count(e => e.EventId.Id == 2302).Should().Be(1);
        latchEpisodes.Sum(m => m.Count).Should().Be(2); // every episode counted, regardless of throttling

        // Episode 3, after the window: a fresh Warning, carrying episode 2's suppressed count.
        time.Advance(TimeSpan.FromSeconds(61));
        await adapter.SendBatchAsync([ToQueue("q", "7"), ToQueue("q", "8"), ToQueue("q", "9")], ct);

        logger.Entries.Count(e => e.EventId.Id == 2301).Should().Be(2);
        LogEntry secondWarning = logger.Entries.Last(e => e.EventId.Id == 2301);
        secondWarning["SuppressedEpisodes"].Should().Be(1);
    }

    [Fact]
    public async Task SendBatchAsync_WhenLoggerThrowsOnLatchWarning_StillRejectsAndReleasesNormally()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.QueueCapacity(2);
                c.ConfigureTopology(t => t.DeclareQueue("q"));
            },
            logger: new ThrowingLogger());

        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(
            [ToQueue("q", "1"), ToQueue("q", "2"), ToQueue("q", "3")], ct);

        results.Count(r => r.IsConfirmed).Should().Be(2);
        results.Count(r => !r.IsConfirmed).Should().Be(1);

        await DrainAndAckAsync(adapter, "q", 2, ct); // latch clears; the throwing logger must not surface here either
    }

    [Fact]
    public async Task Dispose_WhenLatchedAndLoggerThrows_ReturnsEveryBufferAndIncrementsLogFailureCount()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var observer = new CountingBufferPoolObserver();
        InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.QueueCapacity(2);
                c.ConfigureTopology(t => t.DeclareQueue("q"));
            },
            bufferPoolObserver: observer,
            logger: new ThrowingLogger());

        // Fills the queue to capacity and latches it (3rd copy rejected, no consumer) — the OnLatchSet
        // notification already throws here; Dispose() below exercises OnLatchCleared's own throw too, via
        // the ReleaseSlot calls DropRemaining performs while draining the still-full, still-latched queue.
        await adapter.SendBatchAsync([ToQueue("q", "1"), ToQueue("q", "2"), ToQueue("q", "3")], ct);

        Action act = () => adapter.Dispose();

        act.Should().NotThrow();
        adapter.QueueDiagnosticsLogFailureCount.Should().BeGreaterThan(0);
        observer.Outstanding.Should().Be(0);
        observer.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Dispose_WhenShutdownDropsHappenTwiceForSameQueueWithinWindow_LogsWarningOnce()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger<InMemoryTransportAdapter>();
        InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.EnableDefer(TimeSpan.FromMinutes(1));
                c.ConfigureTopology(t => t.DeclareQueue("q"));
            },
            logger: logger);

        await adapter.SendBatchAsync([ToQueue("q", "1"), ToQueue("q", "2")], ct);

        (InboundMessage deferred, IAsyncEnumerator<InboundMessage> e1) = await ReceiveOneAsync(adapter, "q", ct);
        await adapter.SettleAsync(SettlementAction.Defer, deferred, ct); // pending timer still holds q's slot
        deferred.Dispose();

        (InboundMessage unsettled, IAsyncEnumerator<InboundMessage> e2) = await ReceiveOneAsync(adapter, "q", ct);
        // `unsettled` is left unsettled on purpose — still in flight when Dispose runs below.

        adapter.Dispose();

        // One drop reported by the defer scheduler's own shutdown sweep, one by the adapter's in-flight
        // sweep — both for "q", within the same throttle window.
        adapter.DroppedOnShutdownCount.Should().Be(2);
        logger.Entries.Count(e => e.Message.Contains("unsettled delivery", StringComparison.Ordinal)).Should().Be(1);

        unsettled.Dispose();
        await e1.DisposeAsync();
        await e2.DisposeAsync();
    }

    private static MeterListener ListenLatchEpisodesInto(Meter meter, List<(string Queue, long Count)> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name == InMemoryTransportMetrics.LatchEpisodesCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string queue = string.Empty;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "queue")
                {
                    queue = (string)tag.Value!;
                }
            }

            lock (measurements)
            {
                measurements.Add((queue, value));
            }
        });
        listener.Start();
        return listener;
    }
}
