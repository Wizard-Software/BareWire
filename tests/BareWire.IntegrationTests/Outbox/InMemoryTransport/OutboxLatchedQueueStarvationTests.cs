using System.Diagnostics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Outbox;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BareWire.IntegrationTests.Outbox.InMemoryTransport;

/// <summary>
/// Scenario 1: proves that <c>OutboxDispatcher</c> does not starve new rows behind a poison backlog
/// stuck on a permanently latched queue — on a real <c>EfCoreOutboxStore</c> against a temporary SQLite
/// file, driven by a real dispatch loop and a real in-memory transport.
/// </summary>
[Collection(OutboxInMemoryIsolation.Name)]
public sealed class OutboxLatchedQueueStarvationTests
{
    private const int Batch = 8;
    private const int RetryReserve = 2; // Math.Max(1, Batch / 4)
    private const int NewShare = Batch - RetryReserve; // 6
    private const int OldCount = 2 * Batch; // 16
    private const int FreshCount = 24;
    private const int QueueCapacity = 32;

    private static readonly TimeSpan Polling = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(4);

    // A verbatim substring of OutboxRetryDiagnostics's rate-limited Warning log template.
    private const string RetryWarningFragment =
        "was not confirmed by the transport and was released for a deferred retry";

    [Fact]
    public async Task Dispatch_OldRowsToLatchedQueueOnEfSqliteStore_DeliversEveryFreshRowWithinCycleBoundWhileDrainingAndDeferringRejectedRows()
    {
        var probe = new GateProbe();

        await using OutboxInMemoryHost host = await OutboxInMemoryHost.StartAsync(
            transport: t =>
            {
                t.QueueCapacity(QueueCapacity);
                t.SendTimeout(TimeSpan.FromMilliseconds(200));
                t.MapExchange<StuckRow>("s1-stuck");
                t.MapExchange<FreshRow>("s1-healthy");
                t.ConfigureTopology(topo =>
                {
                    topo.DeclareExchange("s1-stuck", ExchangeType.Fanout, durable: false);
                    topo.DeclareExchange("s1-healthy", ExchangeType.Fanout, durable: false);
                    topo.DeclareQueue("s1-stuck");
                    topo.DeclareQueue("s1-healthy");
                    topo.BindExchangeToQueue("s1-stuck", "s1-stuck", string.Empty);
                    topo.BindExchangeToQueue("s1-healthy", "s1-healthy", string.Empty);
                });
                t.ReceiveEndpoint("s1-stuck", e => e.Consumer<StuckRowConsumer, StuckRow>());
                t.ReceiveEndpoint("s1-healthy", e => e.Consumer<FreshRowConsumer, FreshRow>());
            },
            outbox: c =>
            {
                c.DispatchBatchSize = Batch;
                c.PollingInterval = Polling;
                c.OutboxLockTimeout = LockTimeout;
            },
            storeKind: OutboxStoreKind.EfCoreSqlite,
            services: s => s
                .AddSingleton(probe)
                .AddTransient<StuckRowConsumer>()
                .AddTransient<FreshRowConsumer>(),
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            // 1. Latch "s1-stuck": flood well past capacity so the gated consumer cannot drain it.
            const int driveCount = QueueCapacity * 2;
            var drive = new List<Task>(driveCount);
            for (int i = 0; i < driveCount; i++)
            {
                drive.Add(host.Bus.Bus.PublishAsync(new StuckRow(-(i + 1)), TestContext.Current.CancellationToken));
            }

            await Task.WhenAll(drive).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => host.Bus.Adapter.Broker.TryGetQueue("s1-stuck", out InMemoryQueue? q) && q.IsLatched,
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("the drive burst above must actually latch the queue, or this scenario proves nothing");

            // 2. Seed the poison backlog directly through the store's API, then start the dispatcher.
            IReadOnlyList<StuckRow> oldMessages = [.. Enumerable.Range(1, OldCount).Select(n => new StuckRow(n))];
            IReadOnlyList<Guid> oldIds = await host.SaveRowsAsync(oldMessages, "s1-stuck");

            host.StartDispatcher();

            // 3. Warm-up: every old row has been claimed and nacked at least once before the measured
            //    fresh-row window starts.
            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => oldIds.All(id => host.Cycles.AttemptCyclesOf(id).Count >= 1)
                        && host.CounterTotal(OutboxRetryDiagnostics.RetriedRowsCounterName) >= OldCount,
                    TimeSpan.FromSeconds(20),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("every old row must be claimed and nacked at least once before the poison backlog is fully established");

            // 4. Warm the healthy path (JIT + EF query compilation) with one row NOT counted toward
            //    FreshCount, before reading cycleAtSave, so the measured window pays none of that cost.
            Guid warmupId = await host.SaveRowAsync(new FreshRow(0), "s1-healthy");
            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => host.Cycles.DeliveredInCycle(warmupId) is not null,
                    TimeSpan.FromSeconds(20),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("the warm-up row must be delivered before the measured fresh rows are seeded");

            int cycleAtSave = host.Cycles.CycleCount;
            IReadOnlyList<FreshRow> freshMessages = [.. Enumerable.Range(1, FreshCount).Select(n => new FreshRow(n))];
            IReadOnlyList<Guid> freshIds = await host.SaveRowsAsync(freshMessages, "s1-healthy");

            // 5. Every fresh row is eventually delivered — a starved dispatcher would never reach this.
            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => freshIds.All(id => host.Cycles.DeliveredInCycle(id) is not null),
                    TimeSpan.FromSeconds(20),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("every fresh row must be delivered");

            int lastFreshCycle = freshIds.Max(id => host.Cycles.DeliveredInCycle(id)!.Value);
            int cycleBound = ((FreshCount + NewShare - 1) / NewShare) + 1;
            (lastFreshCycle - cycleAtSave).Should().BeLessThanOrEqualTo(
                cycleBound,
                "the retry reserve must never starve new rows: at least NewShare fresh rows are claimed per cycle");

            // Drain assertion: every gap between consecutive fresh-row-claiming cycle STARTS must stay
            // well under one PollingInterval — a paced (paused) loop would show a gap >= PollingInterval,
            // which a single total-span assertion could mask if only one cycle paused.
            List<int> freshClaimCycles = [.. freshIds
                .SelectMany(id => host.Cycles.AttemptCyclesOf(id))
                .Distinct()
                .OrderBy(cycle => cycle)];

            for (int i = 1; i < freshClaimCycles.Count; i++)
            {
                TimeSpan gap = Stopwatch.GetElapsedTime(
                    host.Cycles.CycleStartTimestamp(freshClaimCycles[i - 1]),
                    host.Cycles.CycleStartTimestamp(freshClaimCycles[i]));

                gap.Should().BeLessThan(
                    0.9 * Polling,
                    $"fresh-row batches must be drained back-to-back (cycles {freshClaimCycles[i - 1]} -> {freshClaimCycles[i]}), not one batch per PollingInterval");
            }

            // Every fresh row handled exactly once by the healthy consumer. DeliveredInCycle only proves
            // the transport confirmed the send (the message reached the queue) — actual consumption by
            // FreshRowConsumer can lag slightly behind that, so this waits independently.
            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => Enumerable.Range(1, FreshCount)
                        .All(n => probe.Handled.TryGetValue(("s1-healthy", n), out int count) && count == 1),
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("every fresh row must be handled exactly once by the healthy consumer");

            foreach (int n in Enumerable.Range(1, FreshCount))
            {
                probe.Handled[("s1-healthy", n)].Should().Be(1);
            }

            // 6. Escalation: at least 4 poison rows accumulate 3+ attempts, each spaced by an escalating
            //    deferral (>= 1 x PollingInterval, then >= 2 x PollingInterval), with a 5% timing margin.
            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => oldIds.Count(id => host.Cycles.AttemptCyclesOf(id).Count >= 3) >= 4,
                    TimeSpan.FromSeconds(30),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("at least 4 poison rows must escalate through at least 3 attempts");

            List<Guid> escalated = [.. oldIds.Where(id => host.Cycles.AttemptCyclesOf(id).Count >= 3).Take(4)];
            foreach (Guid id in escalated)
            {
                IReadOnlyList<long> timestamps = host.Cycles.AttemptTimestampsOf(id);

                TimeSpan gap1 = Stopwatch.GetElapsedTime(timestamps[0], timestamps[1]);
                gap1.Should().BeGreaterThanOrEqualTo(
                    0.95 * Polling, $"row {id}'s first retry must wait at least one PollingInterval");

                TimeSpan gap2 = Stopwatch.GetElapsedTime(timestamps[1], timestamps[2]);
                gap2.Should().BeGreaterThanOrEqualTo(
                    0.95 * 2 * Polling, $"row {id}'s second retry must wait at least two PollingIntervals (escalating deferral)");
            }

            // 7. Visibility: the retry counter, the rate-limited Warning log, and the in-memory
            //    transport's own rejection/latch-episode counters for the stuck queue.
            host.CounterTotal(OutboxRetryDiagnostics.RetriedRowsCounterName).Should().BeGreaterThanOrEqualTo(OldCount);
            host.HasLog(LogLevel.Warning, RetryWarningFragment).Should().BeTrue();
            host.Bus.Telemetry
                .CounterTotal(InMemoryTransportMetrics.RejectedCounterName, (InMemoryTransportMetrics.QueueTag, "s1-stuck"))
                .Should().BeGreaterThan(0);
            host.Bus.Telemetry
                .CounterTotal(InMemoryTransportMetrics.LatchEpisodesCounterName, (InMemoryTransportMetrics.QueueTag, "s1-stuck"))
                .Should().BeGreaterThan(0);
        }
        finally
        {
            // Release before DisposeAsync so the dispatcher's StopAsync / drain does not hang on the
            // gated consumer.
            probe.StuckGate.TrySetResult();
        }
    }
}
