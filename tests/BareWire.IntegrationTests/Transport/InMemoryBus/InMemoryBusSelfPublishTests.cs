using System.Collections.Concurrent;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.IntegrationTests.Transport.InMemoryBus;

/// <summary>
/// Scenario 2: a consumer that publishes back into its own, already-full queue must not
/// hang the bus's publish loop — an unrelated healthy queue published to concurrently must still receive
/// every one of its own messages, and the transport must record rejections for the full "self" queue.
/// </summary>
[Collection(InMemoryBusIsolation.Name)]
public sealed class InMemoryBusSelfPublishTests
{
    private const string SelfQueue = "self";
    private const string OtherQueue = "other";
    private const int QueueCapacity = 10;
    private const int SelfFloodCount = 50;
    private const int OtherCount = 20;

    // Kept at or below half of QueueCapacity: a full "self" spends the bus's single
    // one-wait-per-SendBatchAsync-call budget, and any OTHER full queue reached later in that same call
    // would be rejected outright (no wait left) rather than waited on. Keeping "other" always below
    // capacity avoids that entirely, regardless of how badly "self" is overflowing.
    private const int OtherWindow = QueueCapacity / 2;

    [Fact]
    public async Task PublishAsync_ConsumerPublishesIntoItsOwnFullQueue_PublishLoopDoesNotHangAndOtherQueueReceives()
    {
        var probe = new SelfPublishProbe();

        await using InMemoryBusHost host = await InMemoryBusHost.StartAsync(
            transport: t =>
            {
                t.DefaultExchange("");
                t.AutoDeclareEndpointQueues();
                t.QueueCapacity(QueueCapacity);
                t.SendTimeout(TimeSpan.FromMilliseconds(200));
                t.MapRoutingKey<SelfMessage>(SelfQueue);
                t.MapRoutingKey<OtherMessage>(OtherQueue);
                t.ReceiveEndpoint(SelfQueue, e =>
                {
                    e.ConcurrentMessageLimit = 1;
                    e.Consumer<SelfConsumer, SelfMessage>();
                });
                t.ReceiveEndpoint(OtherQueue, e => e.Consumer<OtherConsumer, OtherMessage>());
            },
            services: s => s.AddSingleton(probe).AddTransient<SelfConsumer>().AddTransient<OtherConsumer>(),
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            // Seed message: on receiving it, the consumer's handler floods 50 publishes back into
            // "self" (its own, soon-to-be-full queue) and then blocks on a gate — this is what fills
            // and keeps "self" full for the rest of the test.
            await host.Bus.PublishAsync(new SelfMessage(0), TestContext.Current.CancellationToken);
            await probe.SelfHandlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Meanwhile, publish to the healthy "other" queue — paced in windows so it is never found
            // full at reservation time (see OtherWindow remarks above).
            for (int published = 0; published < OtherCount;)
            {
                int batchSize = Math.Min(OtherWindow, OtherCount - published);
                var batch = new List<Task>(batchSize);
                for (int i = 0; i < batchSize; i++)
                {
                    batch.Add(host.Bus.PublishAsync(new OtherMessage(published + i), TestContext.Current.CancellationToken));
                }

                await Task.WhenAll(batch).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                published += batchSize;

                bool paced = await WaitUntilAsync(
                    () => probe.OtherReceived.Count >= published - OtherWindow,
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken);

                if (!paced)
                {
                    // Fail fast instead of publishing the remaining batches against a stalled consumer.
                    throw new TimeoutException(
                        $"Pacing wait timed out after 10s at published={published}: "
                        + $"received={probe.OtherReceived.Count}, required>={published - OtherWindow}.");
                }
            }

            await WaitUntilAsync(
                () => probe.OtherReceived.Count >= OtherCount,
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            await probe.SelfFloodCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            probe.OtherReceived.Should().BeEquivalentTo(Enumerable.Range(0, OtherCount));

            host.Telemetry
                .CounterTotal(InMemoryTransportMetrics.RejectedCounterName, (InMemoryTransportMetrics.QueueTag, SelfQueue))
                .Should().BeGreaterThan(0);

            host.Telemetry
                .CounterTotal(InMemoryTransportMetrics.RejectedCounterName, (InMemoryTransportMetrics.QueueTag, OtherQueue))
                .Should().Be(0);

            // Sanity check only — PublishAsync merely writes to the bus's own
            // outgoing channel, so this proves the consumer's 50 calls returned, nothing about whether
            // the transport actually accepted any of them. The real proof of "self" overflowing is the
            // rejected-counter assertion above.
            probe.SelfFloodCompleted.Task.IsCompletedSuccessfully.Should().BeTrue();
        }
        finally
        {
            // Release the blocked consumer BEFORE the host is disposed (via the enclosing `await using`),
            // so DisposeAsync's drain/StopAsync does not hang.
            probe.SelfGate.TrySetResult();
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
            // Timed out — the caller's own assertion reports the unmet condition.
            return false;
        }
    }

    // ── Messages ───────────────────────────────────────────────────────────────────────────────────

    private sealed record SelfMessage(int Id);

    private sealed record OtherMessage(int Id);

    // ── Probe (per-test singleton, injected into consumers — no static mutable state) ────────────────

    private sealed class SelfPublishProbe
    {
        public TaskCompletionSource SelfHandlerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SelfFloodCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SelfGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentBag<int> OtherReceived { get; } = [];
    }

    // ── Consumers ──────────────────────────────────────────────────────────────────────────────────

    private sealed class SelfConsumer(IBus bus, SelfPublishProbe probe) : IConsumer<SelfMessage>
    {
        public async Task ConsumeAsync(ConsumeContext<SelfMessage> context)
        {
            if (context.Message.Id != 0)
            {
                // One of the flooded messages that happened to fit into "self" before it filled up.
                // ConcurrentMessageLimit(1) on this endpoint means this can only run once the seed
                // message's handler below has returned (after the gate is released) — settle it and move on.
                return;
            }

            probe.SelfHandlerStarted.TrySetResult();

            var flood = new List<Task>(SelfFloodCount);
            for (int i = 1; i <= SelfFloodCount; i++)
            {
                flood.Add(bus.PublishAsync(new SelfMessage(i), context.CancellationToken));
            }

            // PublishAsync only writes to the bus's own outgoing channel (BareWireBus.cs) — this proves
            // the calls returned, not that the transport accepted any of the 50 sends.
            await Task.WhenAll(flood).WaitAsync(TimeSpan.FromSeconds(10), context.CancellationToken).ConfigureAwait(false);
            probe.SelfFloodCompleted.TrySetResult();

            await probe.SelfGate.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class OtherConsumer(SelfPublishProbe probe) : IConsumer<OtherMessage>
    {
        public Task ConsumeAsync(ConsumeContext<OtherMessage> context)
        {
            probe.OtherReceived.Add(context.Message.Id);
            return Task.CompletedTask;
        }
    }
}
