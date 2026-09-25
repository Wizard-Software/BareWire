using System.Collections.Concurrent;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.IntegrationTests.Transport.InMemoryBus;

/// <summary>
/// Scenario 3 (task 20.28): a bus-loop batch that mixes an oversized message with healthy ones —
/// the oversized message must be rejected on its own without losing or blocking the rest of the batch.
/// </summary>
[Collection(InMemoryBusIsolation.Name)]
public sealed class InMemoryBusMixedBatchTests
{
    private const int MaxMessageSizeBytes = 1024;
    private const string GateQueue = "gate-mixed";
    private const string MixedQueue = "mixed";

    // Large enough that the "mixed" queue never comes close to being full while holding all 31
    // messages of the real batch, but small enough that filling "gate-mixed" to capacity (see the
    // gate technique below) with plain integer-payload messages is cheap. QueueCapacity is a
    // transport-WIDE option (BareWire.CLAUDE.md / GAP-5 mitigation) — it applies to both queues.
    private const int QueueCapacity = 40;

    [Fact]
    public async Task PublishAsync_OneOversizedMessageInsideBusLoopBatch_RemainingMessagesAreDelivered()
    {
        var probe = new MixedBatchProbe();
        const string uniqueMarker = "mixed-batch-marker-7f3e1c";

        await using InMemoryBusHost host = await InMemoryBusHost.StartAsync(
            transport: t =>
            {
                t.DefaultExchange("");
                t.AutoDeclareEndpointQueues();
                t.QueueCapacity(QueueCapacity);
                t.MaxMessageSize(MaxMessageSizeBytes);
                t.SendTimeout(TimeSpan.FromSeconds(3));
                t.MapRoutingKey<GateMessage>(GateQueue);
                t.MapRoutingKey<MixedMessage>(MixedQueue);
                t.ReceiveEndpoint(GateQueue, e => e.Consumer<GateConsumer, GateMessage>());
                t.ReceiveEndpoint(MixedQueue, e => e.Consumer<MixedConsumer, MixedMessage>());
            },
            services: s => s.AddSingleton(probe).AddTransient<GateConsumer>().AddTransient<MixedConsumer>(),
            cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            // Fill "gate-mixed" to capacity: every one of these sends finds room (occupancy climbs
            // 0 -> QueueCapacity without ever hitting "full"), so all are accepted without waiting.
            // The queue's one consumer (GateConsumer) blocks on the first message it is handed, so
            // none of these ever settle — occupancy stays AT capacity for the rest of the test.
            var fillers = new List<Task>(QueueCapacity);
            for (int i = 0; i < QueueCapacity; i++)
            {
                fillers.Add(host.Bus.PublishAsync(new GateMessage(i), TestContext.Current.CancellationToken));
            }

            await Task.WhenAll(fillers).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await probe.GateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // One more send to the now-full, actively-consumed "gate-mixed" queue. The bus publisher
            // loop's single SendBatchAsync call for it stalls inside the SendTimeout wait configured
            // above. Give the loop time to read this message ALONE (the channel is otherwise empty at
            // this point) and enter that wait before publishing anything else below — this is what
            // guarantees the 31 "mixed" messages cannot be split across more than one SendBatchAsync
            // call (GAP-5 mitigation): they accumulate in the bus's outgoing channel while the loop is
            // busy stalled on this send, and are all drained together once it returns.
            await host.Bus.PublishAsync(new GateMessage(QueueCapacity), TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

            var smallPayload = new byte[16];
            var oversizedPayload = new byte[MaxMessageSizeBytes + 512];

            var mixedPublishes = new List<Task>(31);
            for (int i = 0; i < 15; i++)
            {
                mixedPublishes.Add(host.Bus.PublishAsync(new MixedMessage($"small-{i}", smallPayload), TestContext.Current.CancellationToken));
            }

            mixedPublishes.Add(host.Bus.PublishAsync(new MixedMessage(uniqueMarker, oversizedPayload), TestContext.Current.CancellationToken));

            for (int i = 15; i < 30; i++)
            {
                mixedPublishes.Add(host.Bus.PublishAsync(new MixedMessage($"small-{i}", smallPayload), TestContext.Current.CancellationToken));
            }

            await Task.WhenAll(mixedPublishes).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await WaitUntilAsync(
                () => Task.FromResult(probe.Received.Count >= 30),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            probe.Received.Should().HaveCount(30);
            probe.Received.Should().NotContain(m => m.Payload.Length > MaxMessageSizeBytes);

            host.Telemetry
                .CounterTotal(InMemoryTransportMetrics.RejectedCounterName, (InMemoryTransportMetrics.ReasonTag, "oversized"))
                .Should().Be(1);

            // SEC-1 hardening: the rejection log/metric path never includes message bodies — assert it
            // directly by checking the unique marker placed inside the oversized payload never leaks
            // into any captured log (formatted message or exception text).
            host.Telemetry.Logs.Should().NotContain(l => l.Message.Contains(uniqueMarker, StringComparison.Ordinal));
        }
        finally
        {
            // Release the gate consumer BEFORE the host is disposed (via the enclosing `await using`),
            // so DisposeAsync's drain/StopAsync does not hang waiting on a permanently blocked consumer.
            probe.GateRelease.TrySetResult();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            while (!await condition().ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timed out — fall through so the caller's own assertion reports the unmet condition.
        }
    }

    // ── Messages ───────────────────────────────────────────────────────────────────────────────────

    private sealed record GateMessage(int Id);

    private sealed record MixedMessage(string Marker, byte[] Payload);

    // ── Probe (per-test singleton, injected into consumers — no static mutable state) ────────────────

    private sealed class MixedBatchProbe
    {
        public TaskCompletionSource GateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource GateRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentBag<MixedMessage> Received { get; } = [];
    }

    // ── Consumers ──────────────────────────────────────────────────────────────────────────────────

    private sealed class GateConsumer(MixedBatchProbe probe) : IConsumer<GateMessage>
    {
        public async Task ConsumeAsync(ConsumeContext<GateMessage> context)
        {
            probe.GateStarted.TrySetResult();
            await probe.GateRelease.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class MixedConsumer(MixedBatchProbe probe) : IConsumer<MixedMessage>
    {
        public Task ConsumeAsync(ConsumeContext<MixedMessage> context)
        {
            probe.Received.Add(context.Message);
            return Task.CompletedTask;
        }
    }
}
