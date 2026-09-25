using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Outbox;
using BareWire.Outbox;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BareWire.IntegrationTests.Outbox.InMemoryTransport;

/// <summary>
/// Scenario 3: proves that under <c>OrderingMode.PerKey</c>, a confirmed sibling of a nacked head-of-key
/// row — released by the dispatcher's per-key ordering barrier without being marked delivered — is
/// re-sent and the resulting duplicate is deduplicated by the production Inbox, while a keyless row is
/// never held by the barrier and the head itself is eventually confirmed once its queue is unlatched.
/// </summary>
[Collection(OutboxInMemoryIsolation.Name)]
public sealed class OutboxPerKeyBarrierDuplicateTests
{
    private const int Batch = 8;
    private const int QueueCapacity = 16;
    private const string OrderingKeyHeader = "x-ordering-key";

    private static readonly TimeSpan Polling = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(4);

    // A verbatim substring of OutboxDispatcher's LogPerKeyBarrierApplied Debug template.
    private const string BarrierLogFragment = "confirmed sibling(s) behind a nacked head-of-line entry";

    [Fact]
    public async Task Dispatch_PerKeyHeadRejectedWithConfirmedSiblingsInSameBatch_ResentSiblingsAreDeduplicatedByInbox()
    {
        var probe = new GateProbe();

        await using OutboxInMemoryHost host = await OutboxInMemoryHost.StartAsync(
            transport: t =>
            {
                t.QueueCapacity(QueueCapacity);
                t.SendTimeout(TimeSpan.FromMilliseconds(200));
                t.MapExchange<HeadStep>("s3-head");
                t.MapExchange<KeyedStep>("s3-ordered");
                t.ConfigureTopology(topo =>
                {
                    topo.DeclareExchange("s3-head", ExchangeType.Fanout, durable: false);
                    topo.DeclareExchange("s3-ordered", ExchangeType.Fanout, durable: false);
                    topo.DeclareQueue("s3-stuck");
                    topo.DeclareQueue("s3-ordered");
                    topo.BindExchangeToQueue("s3-head", "s3-stuck", string.Empty);
                    topo.BindExchangeToQueue("s3-ordered", "s3-ordered", string.Empty);
                });
                t.ReceiveEndpoint("s3-stuck", e => e.Consumer<HeadStepConsumer, HeadStep>());
                t.ReceiveEndpoint("s3-ordered", e => e.Consumer<KeyedStepConsumer, KeyedStep>());
            },
            outbox: c =>
            {
                c.DispatchBatchSize = Batch;
                c.PollingInterval = Polling;
                c.OutboxLockTimeout = LockTimeout;
                c.OrderingMode = OrderingMode.PerKey;
                c.OrderingKeyHeaderName = OrderingKeyHeader;
                c.AllowDegradedOrdering = true;
            },
            storeKind: OutboxStoreKind.HeadOfLineFreeInMemory,
            services: s => s
                .AddSingleton(probe)
                .AddTransient<HeadStepConsumer>()
                .AddTransient<KeyedStepConsumer>(),
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            // 1. Latch "s3-stuck": it is the only queue bound to "s3-head", so a single unpaced burst
            //    (as in scenario 1) is sufficient — no sibling exchange shares its capacity.
            const int driveCount = QueueCapacity * 2;
            var drive = new List<Task>(driveCount);
            for (int i = 0; i < driveCount; i++)
            {
                drive.Add(host.Bus.Bus.PublishAsync(new HeadStep(-(i + 1)), TestContext.Current.CancellationToken));
            }

            await Task.WhenAll(drive).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => host.Bus.Adapter.Broker.TryGetQueue("s3-stuck", out InMemoryQueue? q) && q.IsLatched,
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("the drive burst above must actually latch 's3-stuck', or this scenario proves nothing");

            // 2. Seed the head (lowest id -> head of key "K"), two siblings sharing key "K", and one
            //    keyless row, each through the store's own API.
            var keyHeaders = new Dictionary<string, string> { [OrderingKeyHeader] = "K" };
            Guid head = await host.SaveRowAsync(new HeadStep(0), "s3-head", keyHeaders);
            Guid sibling1 = await host.SaveRowAsync(new KeyedStep(1), "s3-ordered", keyHeaders);
            Guid sibling2 = await host.SaveRowAsync(new KeyedStep(2), "s3-ordered", keyHeaders);
            Guid keyless = await host.SaveRowAsync(new KeyedStep(100), "s3-ordered");

            host.StartDispatcher();

            // 3. Wait until both siblings and the keyless row are confirmed delivered.
            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => host.Cycles.DeliveredInCycle(sibling1) is not null
                        && host.Cycles.DeliveredInCycle(sibling2) is not null
                        && host.Cycles.DeliveredInCycle(keyless) is not null,
                    TimeSpan.FromSeconds(20),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("both siblings and the keyless row must eventually be confirmed delivered");

            foreach (Guid sibling in new[] { sibling1, sibling2 })
            {
                host.Cycles.BarrierReleasedCount(sibling).Should().BeGreaterThanOrEqualTo(1,
                    "the transport accepted the sibling behind the rejected head, so the barrier released it");
                host.Cycles.AttemptCyclesOf(sibling).Count.Should().Be(host.Cycles.BarrierReleasedCount(sibling) + 1);
            }

            host.Cycles.AttemptCyclesOf(keyless).Should().ContainSingle("keyless rows are never held by the barrier");
            host.HasLog(LogLevel.Debug, BarrierLogFragment).Should().BeTrue();

            // 4. Visible duplicate, deduplicated by the inbox.
            long expected = host.Cycles.BarrierReleasedCount(sibling1) + host.Cycles.BarrierReleasedCount(sibling2);

            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => host.CounterTotal(InboxDiagnostics.DuplicatesCounterName, (InboxDiagnostics.ConsumerTypeTag, "s3-ordered")) == expected,
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("the inbox duplicate counter must eventually settle at the number of barrier-released re-sends");
            host.CounterTotal(InboxDiagnostics.DuplicatesCounterName, (InboxDiagnostics.ConsumerTypeTag, "s3-ordered")).Should().Be(expected);

            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => probe.Handled.GetValueOrDefault(("s3-ordered", 1)) == 1
                        && probe.Handled.GetValueOrDefault(("s3-ordered", 2)) == 1
                        && probe.Handled.GetValueOrDefault(("s3-ordered", 100)) == 1,
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("every ordered row must be handled exactly once despite the barrier-released duplicate");

            probe.Handled[("s3-ordered", 1)].Should().Be(1);
            probe.Handled[("s3-ordered", 2)].Should().Be(1);
            probe.Handled[("s3-ordered", 100)].Should().Be(1);

            // 5. Barrier siblings are not retries: the retried-rows counter counts only head nacks. Read
            //    BEFORE releasing the latch — the head is never delivered yet at this point, so every one
            //    of its attempts is a nack, and the counter only increments after ReleaseLockAsync, one
            //    step after the attempt itself is recorded at GetPendingAsync's start. The wait result is
            //    the assertion: re-reading both values afterwards could race a new head claim.
            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => host.CounterTotal(OutboxRetryDiagnostics.RetriedRowsCounterName) == host.Cycles.AttemptCyclesOf(head).Count,
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("the retried-rows counter must settle at the head's own attempt count before the head is ever delivered");

            // 6. Release the latch -> the head is eventually confirmed.
            probe.StuckGate.TrySetResult();

            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => host.Cycles.DeliveredInCycle(head) is not null,
                    TimeSpan.FromSeconds(30),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("the head must eventually be confirmed delivered once its queue is unlatched");

            (await OutboxInMemoryHost.WaitUntilAsync(
                    () => probe.Handled.GetValueOrDefault(("s3-stuck", 0)) == 1,
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken))
                .Should().BeTrue("the head must eventually be handled exactly once by its consumer");
            probe.Handled[("s3-stuck", 0)].Should().Be(1);
        }
        finally
        {
            // Release before DisposeAsync so the dispatcher's StopAsync / drain does not hang on the
            // gated consumer.
            probe.StuckGate.TrySetResult();
        }
    }
}
