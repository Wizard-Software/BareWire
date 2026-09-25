using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.IntegrationTests.Transport.InMemoryBus;

/// <summary>
/// Scenario 5 (task 20.28): a burst of twice a queue's capacity. With a fast, always-active consumer
/// the queue must never latch and loss must be bounded by the excess over capacity. The negative
/// control (a permanently stalled consumer) proves the "no latch" assertion above is actually
/// sensitive — it fails, with a latch and rejections, under the same burst.
/// </summary>
[Collection(InMemoryBusIsolation.Name)]
public sealed class InMemoryBusBurstTests
{
    private const int Capacity = 500;
    private const int Total = 1_000;

    [Fact]
    public async Task PublishAsync_BurstOfTwiceQueueCapacityWithFastConsumer_NoLatchAndLossBoundedByExcess()
    {
        const string queue = "burst";
        var probe = new BurstProbe();

        await using InMemoryBusHost host = await InMemoryBusHost.StartAsync(
            transport: t =>
            {
                t.DefaultExchange("");
                t.AutoDeclareEndpointQueues();
                t.QueueCapacity(Capacity);
                t.SendTimeout(TimeSpan.FromSeconds(2));
                t.MapRoutingKey<BurstMessage>(queue);
                t.ReceiveEndpoint(queue, e => e.Consumer<FastConsumer, BurstMessage>());
            },
            services: s => s.AddSingleton(probe).AddTransient<FastConsumer>(),
            minimumLogLevel: LogLevel.Information,
            cancellationToken: TestContext.Current.CancellationToken);

        // Warm-up (PERF-5 mitigation): IBusControl.StartAsync can return before the "burst" consume
        // loop has actually begun reading (BareWireBusControl races the loop's first iteration). A
        // single message, waited for delivery, proves the consumer is active before the real burst —
        // otherwise the burst's first wave could find the queue full with NO active consumer yet and
        // latch immediately, which this test is not meant to measure.
        await host.Bus.PublishAsync(new BurstMessage(-1), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => probe.Delivered >= 1, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var publishes = new List<Task>(Total);
        for (int i = 0; i < Total; i++)
        {
            publishes.Add(host.Bus.PublishAsync(new BurstMessage(i), TestContext.Current.CancellationToken));
        }

        // This only confirms all 1,000 messages were accepted into the BUS's own outgoing channel —
        // PublishAsync is fire-and-forget with respect to the transport (BareWireBus.cs), so the actual
        // send/delivery is still in flight after this completes.
        await Task.WhenAll(publishes).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        await WaitUntilAsync(
            () => probe.Delivered - 1 + host.Telemetry.CounterTotal(InMemoryTransportMetrics.RejectedCounterName, (InMemoryTransportMetrics.QueueTag, queue)) >= Total,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        long rejected = host.Telemetry.CounterTotal(InMemoryTransportMetrics.RejectedCounterName, (InMemoryTransportMetrics.QueueTag, queue));
        long delivered = probe.Delivered - 1; // exclude the warm-up message

        host.Telemetry.CounterTotal(InMemoryTransportMetrics.LatchEpisodesCounterName, (InMemoryTransportMetrics.QueueTag, queue)).Should().Be(0);
        rejected.Should().BeLessThanOrEqualTo(Capacity);
        delivered.Should().Be(Total - rejected);
    }

    [Fact]
    public async Task PublishAsync_BurstOfTwiceQueueCapacityWithStalledConsumer_LatchesAndRejects()
    {
        const string queue = "burst-stalled";
        var probe = new BurstProbe();

        await using InMemoryBusHost host = await InMemoryBusHost.StartAsync(
            transport: t =>
            {
                t.DefaultExchange("");
                t.AutoDeclareEndpointQueues();
                t.QueueCapacity(Capacity);
                t.SendTimeout(TimeSpan.FromSeconds(2));
                t.MapRoutingKey<BurstMessage>(queue);
                t.ReceiveEndpoint(queue, e => e.Consumer<StalledConsumer, BurstMessage>());
            },
            services: s => s.AddSingleton(probe).AddTransient<StalledConsumer>(),
            minimumLogLevel: LogLevel.Information,
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            var publishes = new List<Task>(Total);
            for (int i = 0; i < Total; i++)
            {
                publishes.Add(host.Bus.PublishAsync(new BurstMessage(i), TestContext.Current.CancellationToken));
            }

            await Task.WhenAll(publishes).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            await WaitUntilAsync(
                () => host.Telemetry.CounterTotal(InMemoryTransportMetrics.LatchEpisodesCounterName, (InMemoryTransportMetrics.QueueTag, queue)) > 0
                      && host.Telemetry.CounterTotal(InMemoryTransportMetrics.RejectedCounterName, (InMemoryTransportMetrics.QueueTag, queue)) > 0,
                TimeSpan.FromSeconds(15),
                TestContext.Current.CancellationToken);

            host.Telemetry.CounterTotal(InMemoryTransportMetrics.LatchEpisodesCounterName, (InMemoryTransportMetrics.QueueTag, queue)).Should().BeGreaterThan(0);
            host.Telemetry.CounterTotal(InMemoryTransportMetrics.RejectedCounterName, (InMemoryTransportMetrics.QueueTag, queue)).Should().BeGreaterThan(0);
        }
        finally
        {
            // Release the permanently blocked consumer BEFORE the host is disposed (via the enclosing
            // `await using`), so DisposeAsync's drain/StopAsync does not hang.
            probe.Gate.TrySetResult();
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

    private sealed record BurstMessage(int Id);

    // ── Probe (per-test singleton, injected into consumers — no static mutable state) ────────────────

    private sealed class BurstProbe
    {
        private long _delivered;

        public long Delivered => Interlocked.Read(ref _delivered);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RecordDelivered() => Interlocked.Increment(ref _delivered);
    }

    // ── Consumers ──────────────────────────────────────────────────────────────────────────────────

    private sealed class FastConsumer(BurstProbe probe) : IConsumer<BurstMessage>
    {
        public Task ConsumeAsync(ConsumeContext<BurstMessage> context)
        {
            probe.RecordDelivered();
            return Task.CompletedTask;
        }
    }

    private sealed class StalledConsumer(BurstProbe probe) : IConsumer<BurstMessage>
    {
        public async Task ConsumeAsync(ConsumeContext<BurstMessage> context) =>
            await probe.Gate.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
    }
}
