using System.Collections.Concurrent;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Pipeline;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.IntegrationTests.Transport.InMemoryBus;

/// <summary>
/// Scenario 4 (task 20.28): a fanout exchange bound to three healthy queues and one permanently stalled
/// queue. Every healthy subscriber must receive every message regardless of the stalled subscriber's
/// latch state, and a direct <c>SendBatchAsync</c> call issued while the stalled queue is latched must
/// report the whole fan-out as unconfirmed while still delivering to the healthy subscribers.
/// </summary>
[Collection(InMemoryBusIsolation.Name)]
public sealed class InMemoryBusFanOutLatchTests
{
    private const string Exchange = "fan";
    private const string Fan1Queue = "fan-1";
    private const string Fan2Queue = "fan-2";
    private const string Fan3Queue = "fan-3";
    private const string StuckQueue = "fan-stuck";
    private const int QueueCapacity = 20;

    private static readonly string[] HealthyQueues = [Fan1Queue, Fan2Queue, Fan3Queue];

    [Fact]
    public async Task PublishAsync_FanOutWithOneLatchedSubscriber_EveryHealthySubscriberReceivesEveryMessage()
    {
        const int total = 200;

        // Half of QueueCapacity (PERF-2 mitigation): keeps every healthy queue's published-but-not-yet-
        // delivered backlog bounded, so none of them is ever found full at reservation time — a full
        // "fan-stuck" already spends the bus's single one-wait-per-SendBatchAsync-call budget on itself
        // (it is last in the transport's fixed, name-ordered per-message target sequence), so any
        // healthy queue that WAS also found full in that same call would be rejected outright.
        const int window = QueueCapacity / 2;

        var probe = new FanOutProbe();

        await using InMemoryBusHost host = await CreateHostAsync(probe, TestContext.Current.CancellationToken);

        try
        {
            for (int published = 0; published < total;)
            {
                int batchSize = Math.Min(window, total - published);
                var batch = new List<Task>(batchSize);
                for (int i = 0; i < batchSize; i++)
                {
                    batch.Add(host.Bus.PublishAsync(new FanMessage(published + i), TestContext.Current.CancellationToken));
                }

                await Task.WhenAll(batch).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                published += batchSize;

                int publishedSoFar = published;
                await WaitUntilAsync(
                    () => HealthyQueues.All(q => probe.Received[q].Count >= publishedSoFar - window),
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken);
            }

            await WaitUntilAsync(
                () => HealthyQueues.All(q => probe.Received[q].Count >= total),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            int[] expectedIds = [.. Enumerable.Range(0, total)];
            foreach (string queue in HealthyQueues)
            {
                probe.Received[queue].Should().BeEquivalentTo(
                    expectedIds, $"queue '{queue}' must receive every message regardless of '{StuckQueue}' being latched");

                host.Telemetry
                    .CounterTotal(InMemoryTransportMetrics.RejectedCounterName, (InMemoryTransportMetrics.QueueTag, queue))
                    .Should().Be(0);
            }

            host.Telemetry
                .CounterTotal(InMemoryTransportMetrics.LatchEpisodesCounterName, (InMemoryTransportMetrics.QueueTag, StuckQueue))
                .Should().BeGreaterThan(0);

            host.Telemetry
                .CounterTotal(InMemoryTransportMetrics.RejectedCounterName, (InMemoryTransportMetrics.QueueTag, StuckQueue))
                .Should().BeGreaterThan(0);
        }
        finally
        {
            // Release the permanently blocked consumer BEFORE the host is disposed (via the enclosing
            // `await using`), so DisposeAsync's drain/StopAsync does not hang.
            probe.StuckGate.TrySetResult();
        }
    }

    [Fact]
    public async Task SendBatchAsync_FanOutWhileOneSubscriberIsLatched_ReturnsUnconfirmedResult()
    {
        var probe = new FanOutProbe();

        await using InMemoryBusHost host = await CreateHostAsync(probe, TestContext.Current.CancellationToken);

        try
        {
            // Drive "fan-stuck" into latch: its consumer never drains it, so publishing well past
            // capacity fills and latches it. The healthy queues absorb the same burst without issue —
            // their fast consumers keep them close to empty throughout, so no pacing is needed here.
            const int driveCount = QueueCapacity * 2;
            var drive = new List<Task>(driveCount);
            for (int i = 0; i < driveCount; i++)
            {
                drive.Add(host.Bus.PublishAsync(new FanMessage(i), TestContext.Current.CancellationToken));
            }

            await Task.WhenAll(drive).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await WaitUntilAsync(
                () => host.Adapter.Broker.TryGetQueue(StuckQueue, out InMemoryQueue? q) && q.IsLatched,
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            host.Adapter.Broker.TryGetQueue(StuckQueue, out InMemoryQueue? stuckQueue);
            stuckQueue!.IsLatched.Should().BeTrue(
                "the drive burst above must have actually latched the queue, or the SendBatchAsync assertion below proves nothing");

            const int marker = -1;
            IMessageSerializer serializer = host.Services.GetRequiredService<IMessageSerializer>();
            Dictionary<string, string> headers = new()
            {
                ["BW-MessageType"] = nameof(FanMessage),
                ["message-id"] = Guid.NewGuid().ToString(),
                ["BW-Exchange"] = Exchange,
            };

            // Built the same way the bus itself builds an outbound message (BareWireBus.PublishAsync) —
            // see deviation D3 in the task plan: this scenario needs the SendResult that IBus.PublishAsync
            // never exposes to its caller, so it calls the same adapter the bus's own container resolved,
            // through the same internal serialization path, rather than a hand-rolled one.
            OutboundMessage outbound = MessagePipeline.ProcessOutboundAsync(
                new FanMessage(marker), serializer, routingKey: string.Empty, headers, TestContext.Current.CancellationToken);

            IReadOnlyList<SendResult> results =
                await host.Adapter.SendBatchAsync([outbound], TestContext.Current.CancellationToken);

            results.Should().ContainSingle().Which.IsConfirmed.Should().BeFalse();

            await WaitUntilAsync(
                () => HealthyQueues.All(q => probe.Received[q].Contains(marker)),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            foreach (string queue in HealthyQueues)
            {
                probe.Received[queue].Should().Contain(
                    marker, $"the healthy subscriber '{queue}' must still receive the fan-out copy even though '{StuckQueue}' rejected its own");
            }
        }
        finally
        {
            probe.StuckGate.TrySetResult();
        }
    }

    private static Task<InMemoryBusHost> CreateHostAsync(FanOutProbe probe, CancellationToken cancellationToken) =>
        InMemoryBusHost.StartAsync(
            transport: t =>
            {
                t.QueueCapacity(QueueCapacity);
                t.SendTimeout(TimeSpan.FromMilliseconds(200));
                t.MapExchange<FanMessage>(Exchange);
                t.ConfigureTopology(topo =>
                {
                    topo.DeclareExchange(Exchange, ExchangeType.Fanout, durable: false);
                    topo.DeclareQueue(Fan1Queue);
                    topo.DeclareQueue(Fan2Queue);
                    topo.DeclareQueue(Fan3Queue);
                    topo.DeclareQueue(StuckQueue);
                    topo.BindExchangeToQueue(Exchange, Fan1Queue, string.Empty);
                    topo.BindExchangeToQueue(Exchange, Fan2Queue, string.Empty);
                    topo.BindExchangeToQueue(Exchange, Fan3Queue, string.Empty);
                    topo.BindExchangeToQueue(Exchange, StuckQueue, string.Empty);
                });
                t.ReceiveEndpoint(Fan1Queue, e => e.Consumer<Fan1Consumer, FanMessage>());
                t.ReceiveEndpoint(Fan2Queue, e => e.Consumer<Fan2Consumer, FanMessage>());
                t.ReceiveEndpoint(Fan3Queue, e => e.Consumer<Fan3Consumer, FanMessage>());
                t.ReceiveEndpoint(StuckQueue, e => e.Consumer<StuckConsumer, FanMessage>());
            },
            services: s => s
                .AddSingleton(probe)
                .AddTransient<Fan1Consumer>()
                .AddTransient<Fan2Consumer>()
                .AddTransient<Fan3Consumer>()
                .AddTransient<StuckConsumer>(),
            cancellationToken: cancellationToken);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            while (!condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timed out — the caller's own assertion reports the unmet condition.
        }
    }

    // ── Messages ───────────────────────────────────────────────────────────────────────────────────

    private sealed record FanMessage(int Id);

    // ── Probe (per-test singleton, injected into consumers — no static mutable state) ────────────────

    private sealed class FanOutProbe
    {
        public Dictionary<string, ConcurrentBag<int>> Received { get; } =
            HealthyQueues.ToDictionary(q => q, _ => new ConcurrentBag<int>());

        public TaskCompletionSource StuckGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ── Consumers ──────────────────────────────────────────────────────────────────────────────────

    private sealed class Fan1Consumer(FanOutProbe probe) : IConsumer<FanMessage>
    {
        public Task ConsumeAsync(ConsumeContext<FanMessage> context)
        {
            probe.Received[Fan1Queue].Add(context.Message.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class Fan2Consumer(FanOutProbe probe) : IConsumer<FanMessage>
    {
        public Task ConsumeAsync(ConsumeContext<FanMessage> context)
        {
            probe.Received[Fan2Queue].Add(context.Message.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class Fan3Consumer(FanOutProbe probe) : IConsumer<FanMessage>
    {
        public Task ConsumeAsync(ConsumeContext<FanMessage> context)
        {
            probe.Received[Fan3Queue].Add(context.Message.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class StuckConsumer(FanOutProbe probe) : IConsumer<FanMessage>
    {
        public async Task ConsumeAsync(ConsumeContext<FanMessage> context) =>
            await probe.StuckGate.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
    }
}
