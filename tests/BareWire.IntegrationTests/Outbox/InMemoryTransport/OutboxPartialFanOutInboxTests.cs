using System.Diagnostics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Outbox;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BareWire.IntegrationTests.Outbox.InMemoryTransport;

/// <summary>
/// Scenario 2: proves that partial fan-out duplicates — a retried outbox row re-sending its fan-out
/// message to every bound queue, including the two healthy ones that already received and processed an
/// earlier attempt — are deduplicated by the production Inbox (per healthy consumer), that the duplicate
/// rate stays bounded by the deferral schedule while the third subscriber is latched, and that every
/// row is confirmed delivered once the latch is released.
/// </summary>
[Collection(OutboxInMemoryIsolation.Name)]
public sealed class OutboxPartialFanOutInboxTests
{
    private const int Batch = 8;
    private const int QueueCapacity = 16;
    private const int LatchDriveCount = QueueCapacity * 2; // 32
    private const int LatchWindow = QueueCapacity / 2; // 8: windows <= QueueCapacity / 2

    private static readonly TimeSpan Polling = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(4);

    [Fact]
    public async Task Dispatch_FanOutWithLatchedSubscriber_RetriedRowDuplicatesAreDeduplicatedByInboxAndRowIsConfirmedAfterRelease()
    {
        var probe = new GateProbe();

        await using OutboxInMemoryHost host = await OutboxInMemoryHost.StartAsync(
            transport: t =>
            {
                t.QueueCapacity(QueueCapacity);
                t.SendTimeout(TimeSpan.FromMilliseconds(200));
                t.MapExchange<OrderPlaced>("s2-orders");
                t.ConfigureTopology(topo =>
                {
                    topo.DeclareExchange("s2-orders", ExchangeType.Fanout, durable: false);
                    topo.DeclareQueue("s2-sub-a");
                    topo.DeclareQueue("s2-sub-b");
                    topo.DeclareQueue("s2-stuck");
                    topo.BindExchangeToQueue("s2-orders", "s2-sub-a", string.Empty);
                    topo.BindExchangeToQueue("s2-orders", "s2-sub-b", string.Empty);
                    topo.BindExchangeToQueue("s2-orders", "s2-stuck", string.Empty);
                });
                t.ReceiveEndpoint("s2-sub-a", e => e.Consumer<SubAConsumer, OrderPlaced>());
                t.ReceiveEndpoint("s2-sub-b", e => e.Consumer<SubBConsumer, OrderPlaced>());
                t.ReceiveEndpoint("s2-stuck", e => e.Consumer<StuckOrderConsumer, OrderPlaced>());
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
                .AddTransient<SubAConsumer>()
                .AddTransient<SubBConsumer>()
                .AddTransient<StuckOrderConsumer>(),
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            // 1. Latch "s2-stuck": publish fillers in windows <= QueueCapacity / 2, pacing on the two
            //    healthy subscribers between windows so no healthy queue is ever found full at
            //    reservation time by the same SendBatchAsync call that also finds "s2-stuck" full.
            for (int published = 0; published < LatchDriveCount;)
            {
                int batchSize = Math.Min(LatchWindow, LatchDriveCount - published);
                var batch = new List<Task>(batchSize);
                for (int i = 0; i < batchSize; i++)
                {
                    int n = -(published + i + 1);
                    batch.Add(host.Bus.Bus.PublishAsync(new OrderPlaced(n), TestContext.Current.CancellationToken));
                }

                await Task.WhenAll(batch).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                published += batchSize;

                int publishedSoFar = published;
                bool paced = await OutboxInMemoryHost.WaitUntilAsync(
                    () => Enumerable.Range(1, publishedSoFar).All(k =>
                        probe.Handled.ContainsKey(("s2-sub-a", -k)) && probe.Handled.ContainsKey(("s2-sub-b", -k))),
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken);

                if (!paced)
                {
                    throw new TimeoutException(
                        $"Latch pacing wait timed out after 10s at published={publishedSoFar}: "
                        + $"sub-a handled={probe.Handled.Count(kv => kv.Key.Queue == "s2-sub-a")}, "
                        + $"sub-b handled={probe.Handled.Count(kv => kv.Key.Queue == "s2-sub-b")}.");
                }
            }

            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => host.Bus.Adapter.Broker.TryGetQueue("s2-stuck", out InMemoryQueue? q) && q.IsLatched,
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("the filler burst above must actually latch 's2-stuck', or this scenario proves nothing");

            // 2. Seed 5 rows in one write (atomic seeding — no dispatch cycle can ever observe a partial batch).
            IReadOnlyList<OrderPlaced> orders = [.. Enumerable.Range(1, 5).Select(n => new OrderPlaced(n))];
            IReadOnlyList<Guid> rows = await host.SaveRowsAsync(orders, "s2-orders");

            long windowStart = Stopwatch.GetTimestamp();
            host.StartDispatcher();

            // 3. Observe the latched window: wait until every row has >= 4 attempts (a lower
            //    threshold cannot distinguish escalating deferral from a naive fixed-interval retry).
            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => rows.All(id => host.Cycles.AttemptCyclesOf(id).Count >= 4),
                    TimeSpan.FromSeconds(30),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("every row must accumulate at least 4 attempts while the stuck queue is latched, or the duplicate-rate bound below proves nothing");

            // Attempt counts and elapsed time read from one snapshot taken under the recorder's own lock
            // so every row's count reflects the exact same instant of the dispatcher loop.
            IReadOnlyDictionary<Guid, IReadOnlyList<int>> snapshot = host.Cycles.SnapshotAttempts();
            TimeSpan window = Stopwatch.GetElapsedTime(windowStart);
            int bound = MaxAttemptsWithin(window, Polling, LockTimeout);

            foreach (Guid id in rows)
            {
                snapshot[id].Count.Should().BeLessThanOrEqualTo(bound,
                    "a rejected row is re-sent only after its escalating deferral, so the duplicate rate is bounded by the schedule");
            }

            // 4. Release the latch and wait until every row is confirmed delivered.
            probe.StuckGate.TrySetResult();

            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => rows.All(id => host.Cycles.DeliveredInCycle(id) is not null),
                    TimeSpan.FromSeconds(30),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("every row must eventually be confirmed delivered once the latch is released");

            // 5. Duplicate accounting, using the FINAL (post-delivery) attempt counts — a delivered row is
            //    never resent again.
            Dictionary<Guid, int> finalAttempts = rows.ToDictionary(id => id, id => host.Cycles.AttemptCyclesOf(id).Count);
            long expectedDuplicates = finalAttempts.Values.Sum(count => (long)count - 1);

            // Delivery confirmation precedes consumer/inbox processing — wait before asserting equality.
            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => host.CounterTotal(InboxDiagnostics.DuplicatesCounterName, (InboxDiagnostics.ConsumerTypeTag, "s2-sub-a")) == expectedDuplicates
                        && host.CounterTotal(InboxDiagnostics.DuplicatesCounterName, (InboxDiagnostics.ConsumerTypeTag, "s2-sub-b")) == expectedDuplicates,
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("the inbox duplicate counter must eventually settle at the number of resent copies per healthy consumer");

            host.CounterTotal(InboxDiagnostics.DuplicatesCounterName, (InboxDiagnostics.ConsumerTypeTag, "s2-sub-a")).Should().Be(expectedDuplicates);
            host.CounterTotal(InboxDiagnostics.DuplicatesCounterName, (InboxDiagnostics.ConsumerTypeTag, "s2-sub-b")).Should().Be(expectedDuplicates);
            expectedDuplicates.Should().BeGreaterThanOrEqualTo(rows.Count, "every row was re-sent at least once, so the scenario is not vacuous");

            host.CounterTotal(InboxDiagnostics.DuplicatesCounterName, (InboxDiagnostics.ConsumerTypeTag, "s2-sub-a"))
                .Should().BeLessThanOrEqualTo(host.CounterTotal(OutboxRetryDiagnostics.RetriedRowsCounterName),
                    "every duplicate comes from a retried row");

            // 6. Exactly-once handling per healthy subscriber, and the released subscriber sees its own
            //    copy exactly once too.
            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => Enumerable.Range(1, 5).All(n =>
                        probe.Handled.GetValueOrDefault(("s2-sub-a", n)) == 1
                        && probe.Handled.GetValueOrDefault(("s2-sub-b", n)) == 1
                        && probe.Handled.GetValueOrDefault(("s2-stuck", n)) == 1),
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("every row must be handled exactly once by each of the three subscribers");

            foreach (int n in Enumerable.Range(1, 5))
            {
                probe.Handled[("s2-sub-a", n)].Should().Be(1);
                probe.Handled[("s2-sub-b", n)].Should().Be(1);
                probe.Handled[("s2-stuck", n)].Should().Be(1);
            }
        }
        finally
        {
            // Release before DisposeAsync so the dispatcher's StopAsync / drain does not hang on the
            // gated consumer.
            probe.StuckGate.TrySetResult();
        }
    }

    // Upper bound on attempts within `window`: attempt k (k >= 2) happens no earlier than
    // sum_{i=0}^{k-2} min(polling * 2^i, cap) after the first one.
    private static int MaxAttemptsWithin(TimeSpan window, TimeSpan polling, TimeSpan cap)
    {
        int attempts = 1;
        TimeSpan elapsed = TimeSpan.Zero;
        for (int i = 0; ; i++)
        {
            TimeSpan deferral = TimeSpan.FromTicks(Math.Min(polling.Ticks << Math.Min(i, 30), cap.Ticks));
            if (elapsed + deferral > window)
            {
                return attempts;
            }

            elapsed += deferral;
            attempts++;
        }
    }
}
