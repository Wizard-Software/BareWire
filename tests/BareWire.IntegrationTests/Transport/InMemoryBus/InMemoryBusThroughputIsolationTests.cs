using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.IntegrationTests.Transport.InMemoryBus;

/// <summary>
/// Scenario 1 (task 20.28): a consumer of one queue ("iso-a") is stalled and 5% of the scenario's
/// traffic still targets it. Publish throughput to every other queue ("iso-b".."iso-z") must stay at
/// least half of the throughput measured for the same load shape with no traffic to "iso-a" at all,
/// no healthy queue may ever be found full at reservation time, and no publish-channel back-pressure
/// alert may fire in either shape of load.
/// </summary>
/// <remarks>
/// <strong>Why 26 distinct closed generic message types.</strong> <c>BareWireBus.PublishAsync&lt;T&gt;</c>
/// resolves exactly one routing key per message TYPE (not per call), so round-robining a single shared
/// message type across 26 queues via typed <c>PublishAsync</c> is not possible — see the task's
/// verification section 10 (GAP-3/PERF-4). <see cref="IsoMessage{TMarker}"/> is a generic marker record;
/// each of the 26 marker types below (<see cref="QA"/>..<see cref="QZ"/>) closes it into its own type,
/// each mapped via <c>MapRoutingKey&lt;IsoMessage&lt;TMarker&gt;&gt;</c> to its own queue. This is also
/// what makes the scenario exercise the alert-checked path at all: the health-check
/// (<c>CheckPublishHealthAlert</c>) only runs inside typed <c>PublishAsync</c>, never on the
/// endpoint-send path used by other scenarios in this task.
/// </remarks>
[Collection(InMemoryBusIsolation.Name)]
public sealed class InMemoryBusThroughputIsolationTests
{
    private const string StalledQueue = "iso-a";
    private const int QueueCapacity = 100;

    // Upper bound allowed by the spec is half of QueueCapacity (PERF-2 mitigation) — the maximum a
    // healthy queue's published-but-not-yet-delivered backlog may reach before the producer waits for it
    // to catch up. A much smaller value is used here on purpose: this backlog is measured end-to-end
    // (published vs. fully delivered), so it also bounds how many messages can be sitting in the bus's
    // own outgoing channel at once (see MaxPendingPublishes below) — keeping it small keeps sustained
    // publish throughput from outrunning the single publisher loop's drain rate and inflating that
    // channel's own occupancy, while remaining comfortably under half of QueueCapacity=100.
    private const int HealthyWindow = 20;

    // Messages fired per Task.WhenAll wave (per healthy queue, so total wave size is roughly
    // HealthyQueues.Length * BurstRounds). Kept small so the number of concurrent writers into the bus's
    // outgoing channel per wave stays a small fraction of MaxPendingPublishes below.
    private const int BurstRounds = 2;

    // D2/GAP-3 mitigation: low enough that the 90% alert threshold (900) is a small, meaningful number —
    // sensitizes the "no back-pressure alert" assertion instead of it being vacuously true against the
    // library default of 10,000.
    private const int MaxPendingPublishes = 1_000;

    // PERF-3 mitigation: large N so JIT/GC/thread-pool jitter is a small fraction of the measured
    // duration. One measured run publishes this many messages to the healthy queues (b..z combined).
    private const int MessageCount = 20_000;

    // PERF-3 mitigation: best-of-5 across interleaved baseline/scenario runs on the SAME host.
    private const int Repeats = 5;

    // 5% of scenario traffic additionally targets the stalled queue (every 20th healthy message also
    // triggers one publish to "iso-a"). Baseline never publishes to "iso-a" at all.
    private const int StalledEvery = 20;

    private static readonly string[] HealthyQueues =
        [.. "bcdefghijklmnopqrstuvwxyz".Select(c => $"iso-{c}")];

    private static readonly Func<IBus, int, CancellationToken, Task>[] HealthyPublishers =
    [
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QB>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QC>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QD>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QE>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QF>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QG>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QH>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QI>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QJ>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QK>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QL>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QM>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QN>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QO>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QP>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QQ>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QR>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QS>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QT>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QU>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QV>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QW>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QX>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QY>(id), ct),
        static (bus, id, ct) => bus.PublishAsync(new IsoMessage<QZ>(id), ct),
    ];

    // Measured duration is ~9 s; 120 s leaves ample margin while still failing fast (instead of hanging
    // for hours) if the pacing wait below silently regressed. xUnit v3 cancels
    // TestContext.Current.CancellationToken on timeout, which every wait in this test is linked to.
    [Fact(Timeout = 120_000)]
    public async Task PublishAsync_ConsumerOfOneQueueStalledAtFivePercentTraffic_OtherQueuesKeepAtLeastHalfBaselineThroughput()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var probe = new IsoProbe();

        await using InMemoryBusHost host = await CreateHostAsync(probe, ct);

        try
        {
            // Warm-up (PERF-5 mitigation): IBusControl.StartAsync returns before the consume loops are
            // actually reading, so an un-warmed queue could latch on its very first burst regardless of
            // a slow consumer. One confirmed delivery per healthy queue, plus confirmation that the
            // stalled consumer has actually started processing its own first (warm-up) message.
            await WarmUpAsync(host, probe, ct);

            // Drive "iso-a" into an actual LATCH (not merely "full with an active-but-blocked
            // consumer") BEFORE any timing starts, and prove it with a negative control (PERF-3
            // mitigation) — bounded wait, not a fixed sleep. Once latched, further sends to "iso-a" are
            // rejected immediately (no per-SendBatchAsync-call wait budget spent on it), which is what
            // keeps mixing 5% traffic into "iso-a" from stalling the publisher loop's shared batches
            // during the scenario runs below.
            await DriveIntoLatchAsync(host, ct);

            host.Telemetry
                .CounterTotal(InMemoryTransportMetrics.LatchEpisodesCounterName, (InMemoryTransportMetrics.QueueTag, StalledQueue))
                .Should().BeGreaterThan(0, "the drive-to-latch step must have actually latched 'iso-a', or every assertion below proves nothing");
            host.Telemetry
                .CounterTotal(InMemoryTransportMetrics.RejectedCounterName, (InMemoryTransportMetrics.QueueTag, StalledQueue))
                .Should().BeGreaterThan(0, "same as above");

            // One host for warm-up, the latch drive, and every measured run below (PERF-3 mitigation) —
            // a fresh host per repetition would let JIT/GC/thread-pool jitter of whichever run happens
            // first alone decide the ratio. Interleaved runs (baseline, scenario, baseline, ...),
            // best-of-5 (minimum duration) on each side.
            var baselineTimes = new List<TimeSpan>(Repeats);
            var scenarioTimes = new List<TimeSpan>(Repeats);
            for (int repeat = 0; repeat < Repeats; repeat++)
            {
                baselineTimes.Add(await RunAsync(host, probe, includeStalledTraffic: false, ct));
                scenarioTimes.Add(await RunAsync(host, probe, includeStalledTraffic: true, ct));

                // "iso-a" must still be latched between and after every scenario run — otherwise the
                // isolation being measured has quietly gone away partway through the measurement.
                host.Adapter.Broker.TryGetQueue(StalledQueue, out InMemoryQueue? stalled);
                stalled!.IsLatched.Should().BeTrue("'iso-a' must remain latched across the whole measurement");
            }

            TimeSpan baselineBest = baselineTimes.Min();
            TimeSpan scenarioBest = scenarioTimes.Min();
            double ratio = baselineBest.TotalMilliseconds / scenarioBest.TotalMilliseconds;

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"baseline best {baselineBest.TotalMilliseconds:F1} ms, scenario best {scenarioBest.TotalMilliseconds:F1} ms, ratio {ratio:F3} "
                + $"(baseline runs: [{string.Join(", ", baselineTimes.Select(t => t.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)))}], "
                + $"scenario runs: [{string.Join(", ", scenarioTimes.Select(t => t.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)))}])");

            ratio.Should().BeGreaterThanOrEqualTo(0.5,
                $"baseline best {baselineBest.TotalMilliseconds:F1} ms, scenario best {scenarioBest.TotalMilliseconds:F1} ms "
                + $"(baseline runs: [{string.Join(", ", baselineTimes.Select(t => t.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)))}], "
                + $"scenario runs: [{string.Join(", ", scenarioTimes.Select(t => t.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)))}])");

            foreach (string queue in HealthyQueues)
            {
                host.Telemetry
                    .CounterTotal(InMemoryTransportMetrics.RejectedCounterName, (InMemoryTransportMetrics.QueueTag, queue))
                    .Should().Be(0, $"queue '{queue}' must never be found full at reservation time given the pacing window");
            }

            // No publish-channel back-pressure alert in EITHER shape of load.
            // NOTE (GAP-3 mitigation, documented honestly per the task's verification section 10): with
            // the healthy-queue pacing above — and "iso-a" being LATCHED rather than merely full, so its
            // rejections never spend the publisher loop's per-SendBatchAsync-call wait budget — the
            // absence of this log is a CONSEQUENCE of the pacing keeping the outgoing channel's occupancy
            // low, not an independent proof that occupancy itself never grew past the 90% alert
            // threshold. A non-vacuous, independent assertion on the channel's own occupancy is not
            // achievable from this test project without a change to src/** (the channel and its Count
            // are private to BareWireBus) — recorded as an open question in the task's changes log.
            host.Telemetry.HasLog(LogLevel.Warning, "Publish channel back-pressure alert").Should().BeFalse();
        }
        finally
        {
            // Release the permanently blocked consumer BEFORE the host is disposed (via the enclosing
            // `await using`), so DisposeAsync's drain/StopAsync does not hang.
            probe.StalledGate.TrySetResult();
        }
    }

    // ── Host construction ──────────────────────────────────────────────────────────────────────────

    private static Task<InMemoryBusHost> CreateHostAsync(IsoProbe probe, CancellationToken cancellationToken) =>
        InMemoryBusHost.StartAsync(
            transport: t =>
            {
                t.DefaultExchange("");
                t.AutoDeclareEndpointQueues();
                t.QueueCapacity(QueueCapacity);
                t.SendTimeout(TimeSpan.FromMilliseconds(200));

                t.MapRoutingKey<IsoMessage<QA>>(StalledQueue);
                t.ReceiveEndpoint(StalledQueue, e => e.Consumer<StalledConsumer, IsoMessage<QA>>());

                MapHealthyQueue<QB>(t, "iso-b");
                MapHealthyQueue<QC>(t, "iso-c");
                MapHealthyQueue<QD>(t, "iso-d");
                MapHealthyQueue<QE>(t, "iso-e");
                MapHealthyQueue<QF>(t, "iso-f");
                MapHealthyQueue<QG>(t, "iso-g");
                MapHealthyQueue<QH>(t, "iso-h");
                MapHealthyQueue<QI>(t, "iso-i");
                MapHealthyQueue<QJ>(t, "iso-j");
                MapHealthyQueue<QK>(t, "iso-k");
                MapHealthyQueue<QL>(t, "iso-l");
                MapHealthyQueue<QM>(t, "iso-m");
                MapHealthyQueue<QN>(t, "iso-n");
                MapHealthyQueue<QO>(t, "iso-o");
                MapHealthyQueue<QP>(t, "iso-p");
                MapHealthyQueue<QQ>(t, "iso-q");
                MapHealthyQueue<QR>(t, "iso-r");
                MapHealthyQueue<QS>(t, "iso-s");
                MapHealthyQueue<QT>(t, "iso-t");
                MapHealthyQueue<QU>(t, "iso-u");
                MapHealthyQueue<QV>(t, "iso-v");
                MapHealthyQueue<QW>(t, "iso-w");
                MapHealthyQueue<QX>(t, "iso-x");
                MapHealthyQueue<QY>(t, "iso-y");
                MapHealthyQueue<QZ>(t, "iso-z");
            },
            services: s =>
            {
                s.AddSingleton(probe);

                // Registered AFTER AddBareWire's own default (inside InMemoryBusHost.StartAsync) wins
                // resolution — Microsoft.Extensions.DependencyInjection resolves the LAST registration
                // of a given service type. Low on purpose (D2/GAP-3 mitigation): sensitizes the 90%
                // back-pressure alert threshold against the library's 10,000 default.
                s.AddSingleton(new PublishFlowControlOptions { MaxPendingPublishes = MaxPendingPublishes });

                s.AddTransient<StalledConsumer>();
                RegisterHealthyConsumer<QB>(s);
                RegisterHealthyConsumer<QC>(s);
                RegisterHealthyConsumer<QD>(s);
                RegisterHealthyConsumer<QE>(s);
                RegisterHealthyConsumer<QF>(s);
                RegisterHealthyConsumer<QG>(s);
                RegisterHealthyConsumer<QH>(s);
                RegisterHealthyConsumer<QI>(s);
                RegisterHealthyConsumer<QJ>(s);
                RegisterHealthyConsumer<QK>(s);
                RegisterHealthyConsumer<QL>(s);
                RegisterHealthyConsumer<QM>(s);
                RegisterHealthyConsumer<QN>(s);
                RegisterHealthyConsumer<QO>(s);
                RegisterHealthyConsumer<QP>(s);
                RegisterHealthyConsumer<QQ>(s);
                RegisterHealthyConsumer<QR>(s);
                RegisterHealthyConsumer<QS>(s);
                RegisterHealthyConsumer<QT>(s);
                RegisterHealthyConsumer<QU>(s);
                RegisterHealthyConsumer<QV>(s);
                RegisterHealthyConsumer<QW>(s);
                RegisterHealthyConsumer<QX>(s);
                RegisterHealthyConsumer<QY>(s);
                RegisterHealthyConsumer<QZ>(s);
            },
            // D4/section-10 mitigation: Information, not the host default of Trace — no per-message log
            // exists on this hot path today, but capturing at Trace would still allocate a CapturedLog
            // per entry for no reason while a throughput measurement is running.
            minimumLogLevel: LogLevel.Information,
            cancellationToken: cancellationToken);

    private static void MapHealthyQueue<TMarker>(IInMemoryConfigurator transport, string queueName)
        where TMarker : IIsoQueueMarker
    {
        transport.MapRoutingKey<IsoMessage<TMarker>>(queueName);
        transport.ReceiveEndpoint(queueName, e => e.Consumer<IsoConsumer<TMarker>, IsoMessage<TMarker>>());
    }

    private static void RegisterHealthyConsumer<TMarker>(IServiceCollection services)
        where TMarker : IIsoQueueMarker
        => services.AddTransient<IsoConsumer<TMarker>>();

    private static Task PublishStalledAsync(IBus bus, int id, CancellationToken cancellationToken) =>
        bus.PublishAsync(new IsoMessage<QA>(id), cancellationToken);

    // ── Warm-up / latch drive ──────────────────────────────────────────────────────────────────────

    private static async Task WarmUpAsync(InMemoryBusHost host, IsoProbe probe, CancellationToken cancellationToken)
    {
        var warmup = new RunState(HealthyQueues.Length);
        probe.CurrentRun = warmup;

        var tasks = new List<Task>(HealthyPublishers.Length + 1) { PublishStalledAsync(host.Bus, -1, cancellationToken) };
        foreach (Func<IBus, int, CancellationToken, Task> publisher in HealthyPublishers)
        {
            tasks.Add(publisher(host.Bus, -1, cancellationToken));
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        await warmup.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        await probe.StalledStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        probe.CurrentRun = null;
    }

    private static async Task DriveIntoLatchAsync(InMemoryBusHost host, CancellationToken cancellationToken)
    {
        const int driveCount = QueueCapacity * 2;
        var drive = new List<Task>(driveCount);
        for (int i = 0; i < driveCount; i++)
        {
            drive.Add(PublishStalledAsync(host.Bus, i, cancellationToken));
        }

        await Task.WhenAll(drive).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        await WaitUntilAsync(
            () => host.Adapter.Broker.TryGetQueue(StalledQueue, out InMemoryQueue? q) && q.IsLatched,
            TimeSpan.FromSeconds(10),
            cancellationToken);
    }

    // ── Measured runs ──────────────────────────────────────────────────────────────────────────────

    private static async Task<TimeSpan> RunAsync(
        InMemoryBusHost host, IsoProbe probe, bool includeStalledTraffic, CancellationToken cancellationToken)
    {
        var run = new RunState(MessageCount);
        probe.CurrentRun = run;

        var stopwatch = Stopwatch.StartNew();
        await PublishHealthyTrafficAsync(host.Bus, run, includeStalledTraffic, cancellationToken).ConfigureAwait(false);

        // Completion signalled via a TCS set by the consumer that observes the target count (PERF-3
        // mitigation) rather than polled — avoids adding a fixed polling-interval tail to the very
        // duration being measured.
        await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        probe.CurrentRun = null;

        run.Total.Should().Be(MessageCount, "every message published to a healthy queue in this run must be delivered");
        foreach (string queue in HealthyQueues)
        {
            run.DeliveredTo(queue).Should().Be(
                MessageCount / HealthyQueues.Length, $"queue '{queue}' must receive its exact, evenly round-robined share");
        }

        return stopwatch.Elapsed;
    }

    private static async Task PublishHealthyTrafficAsync(
        IBus bus, RunState run, bool includeStalledTraffic, CancellationToken cancellationToken)
    {
        int healthyCount = HealthyQueues.Length;
        int burstSize = healthyCount * BurstRounds;
        int published = 0;
        int queueIndex = 0;
        int stalledId = 0;

        while (published < MessageCount)
        {
            int thisBurst = Math.Min(burstSize, MessageCount - published);
            var batch = new List<Task>(thisBurst + (includeStalledTraffic ? (thisBurst / StalledEvery) + 1 : 0));

            for (int i = 0; i < thisBurst; i++)
            {
                int id = published + i;
                batch.Add(HealthyPublishers[queueIndex](bus, id, cancellationToken));
                queueIndex = (queueIndex + 1) % healthyCount;

                if (includeStalledTraffic && (id + 1) % StalledEvery == 0)
                {
                    batch.Add(PublishStalledAsync(bus, stalledId, cancellationToken));
                    stalledId++;
                }
            }

            await Task.WhenAll(batch).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            published += thisBurst;

            int publishedPerQueueSoFar = published / healthyCount;
            bool paced = await WaitUntilAsync(
                () => HealthyQueues.All(q => run.DeliveredTo(q) >= publishedPerQueueSoFar - HealthyWindow),
                TimeSpan.FromSeconds(20),
                cancellationToken);

            if (!paced)
            {
                // Fail fast here instead of silently continuing to publish: a stalled healthy consumer
                // would otherwise make every one of the ~390 remaining bursts wait out this same 20 s
                // timeout, turning a fast, diagnosable failure into a multi-hour hang before the
                // run-level WaitAsync(20 s) around the caller eventually throws with no clue as to cause.
                string perQueueDiagnostics = string.Join(", ", HealthyQueues.Select(q =>
                    $"{q}: delivered={run.DeliveredTo(q)} (published~={publishedPerQueueSoFar}, required>={publishedPerQueueSoFar - HealthyWindow})"));
                throw new TimeoutException(
                    $"Pacing wait timed out after 20s: healthy queues did not catch up to published={published} "
                    + $"(publishedPerQueueSoFar={publishedPerQueueSoFar}, window={HealthyWindow}). Per-queue delivered vs. published: {perQueueDiagnostics}");
            }
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            while (!condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), linkedCts.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timed out — the caller decides whether to fail fast or let its own assertion report it.
            return false;
        }
    }

    // ── Messages ───────────────────────────────────────────────────────────────────────────────────

    private sealed record IsoMessage<TMarker>(int Id)
        where TMarker : IIsoQueueMarker;

    // ── Queue markers — one closed generic type per queue (see the class-level remarks) ──────────────

    private interface IIsoQueueMarker
    {
        static abstract string QueueName { get; }
    }

    private sealed class QA : IIsoQueueMarker { public static string QueueName => "iso-a"; }
    private sealed class QB : IIsoQueueMarker { public static string QueueName => "iso-b"; }
    private sealed class QC : IIsoQueueMarker { public static string QueueName => "iso-c"; }
    private sealed class QD : IIsoQueueMarker { public static string QueueName => "iso-d"; }
    private sealed class QE : IIsoQueueMarker { public static string QueueName => "iso-e"; }
    private sealed class QF : IIsoQueueMarker { public static string QueueName => "iso-f"; }
    private sealed class QG : IIsoQueueMarker { public static string QueueName => "iso-g"; }
    private sealed class QH : IIsoQueueMarker { public static string QueueName => "iso-h"; }
    private sealed class QI : IIsoQueueMarker { public static string QueueName => "iso-i"; }
    private sealed class QJ : IIsoQueueMarker { public static string QueueName => "iso-j"; }
    private sealed class QK : IIsoQueueMarker { public static string QueueName => "iso-k"; }
    private sealed class QL : IIsoQueueMarker { public static string QueueName => "iso-l"; }
    private sealed class QM : IIsoQueueMarker { public static string QueueName => "iso-m"; }
    private sealed class QN : IIsoQueueMarker { public static string QueueName => "iso-n"; }
    private sealed class QO : IIsoQueueMarker { public static string QueueName => "iso-o"; }
    private sealed class QP : IIsoQueueMarker { public static string QueueName => "iso-p"; }
    private sealed class QQ : IIsoQueueMarker { public static string QueueName => "iso-q"; }
    private sealed class QR : IIsoQueueMarker { public static string QueueName => "iso-r"; }
    private sealed class QS : IIsoQueueMarker { public static string QueueName => "iso-s"; }
    private sealed class QT : IIsoQueueMarker { public static string QueueName => "iso-t"; }
    private sealed class QU : IIsoQueueMarker { public static string QueueName => "iso-u"; }
    private sealed class QV : IIsoQueueMarker { public static string QueueName => "iso-v"; }
    private sealed class QW : IIsoQueueMarker { public static string QueueName => "iso-w"; }
    private sealed class QX : IIsoQueueMarker { public static string QueueName => "iso-x"; }
    private sealed class QY : IIsoQueueMarker { public static string QueueName => "iso-y"; }
    private sealed class QZ : IIsoQueueMarker { public static string QueueName => "iso-z"; }

    // ── Probe / per-run delivery tracking (per-test singleton, no static mutable state) ──────────────

    private sealed class IsoProbe
    {
        private RunState? _currentRun;

        /// <summary>The run currently accumulating delivered-message counts, or <see langword="null"/> between runs.</summary>
        public RunState? CurrentRun
        {
            get => Volatile.Read(ref _currentRun);
            set => Volatile.Write(ref _currentRun, value);
        }

        /// <summary>Set once the stalled consumer has actually started processing its first (warm-up) message.</summary>
        public TaskCompletionSource StalledStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Released once, at the end of the test, to unblock the permanently stalled consumer for teardown.</summary>
        public TaskCompletionSource StalledGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Tracks per-queue and total delivered-message counts for exactly one measured (or warm-up) run.</summary>
    private sealed class RunState(int target)
    {
        private readonly ConcurrentDictionary<string, long> _delivered = new();
        private long _total;

        /// <summary>Completes once <see cref="Total"/> reaches <paramref name="target"/> — set by whichever delivery observes that.</summary>
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RecordDelivery(string queueName)
        {
            _delivered.AddOrUpdate(queueName, 1, static (_, count) => count + 1);
            if (Interlocked.Increment(ref _total) >= target)
            {
                Completion.TrySetResult();
            }
        }

        public long DeliveredTo(string queueName) => _delivered.GetValueOrDefault(queueName);

        public long Total => Interlocked.Read(ref _total);
    }

    // ── Consumers ──────────────────────────────────────────────────────────────────────────────────

    private sealed class IsoConsumer<TMarker>(IsoProbe probe) : IConsumer<IsoMessage<TMarker>>
        where TMarker : IIsoQueueMarker
    {
        public Task ConsumeAsync(ConsumeContext<IsoMessage<TMarker>> context)
        {
            probe.CurrentRun?.RecordDelivery(TMarker.QueueName);
            return Task.CompletedTask;
        }
    }

    private sealed class StalledConsumer(IsoProbe probe) : IConsumer<IsoMessage<QA>>
    {
        public async Task ConsumeAsync(ConsumeContext<IsoMessage<QA>> context)
        {
            probe.StalledStarted.TrySetResult();
            await probe.StalledGate.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        }
    }
}
