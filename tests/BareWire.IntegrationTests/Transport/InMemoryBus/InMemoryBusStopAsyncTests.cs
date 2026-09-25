using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Pipeline;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.IntegrationTests.Transport.InMemoryBus;

/// <summary>
/// Scenario 6 (task 20.28): <c>StopAsync</c>/<c>DisposeAsync</c> ordering and timing on the in-memory
/// bus. <see cref="BareWire.Bus.BareWireBusControl.StopAsync"/> drains in-flight work (bounded by
/// <c>DrainTimeout</c>) BEFORE cancelling consume loops, so genuinely in-flight messages — including a
/// follow-up published from inside a handler — get a chance to complete; a queue with no active
/// consumer never extends that drain; and once consume loops are cancelled, the transport adapter never
/// waits out a full queue again — messages still sitting in the bus's own outgoing channel, or still
/// held by the adapter, are rejected/dropped immediately and reported once, per queue, when the adapter
/// itself is disposed (<c>InMemoryTransportAdapter.Dispose</c>, not <c>StopAsync</c> itself).
/// </summary>
[Collection(InMemoryBusIsolation.Name)]
public sealed class InMemoryBusStopAsyncTests
{
    [Fact]
    public async Task StopAsync_InFlightMessagesWithFollowUpPublishes_DrainsAndDeliversFollowUpsBeforeCancelling()
    {
        const string inQueue = "stop-in";
        const string outQueue = "stop-out";
        const int messageCount = 20;
        var probe = new FollowUpProbe();

        await using InMemoryBusHost host = await InMemoryBusHost.StartAsync(
            transport: t =>
            {
                t.DefaultExchange("");
                t.AutoDeclareEndpointQueues();
                t.QueueCapacity(50);
                t.SendTimeout(TimeSpan.FromSeconds(3));
                t.DrainTimeout(TimeSpan.FromSeconds(30));
                t.MapRoutingKey<StopInMessage>(inQueue);
                t.MapRoutingKey<StopOutMessage>(outQueue);
                t.ReceiveEndpoint(inQueue, e => e.Consumer<StopInConsumer, StopInMessage>());
                t.ReceiveEndpoint(outQueue, e => e.Consumer<StopOutConsumer, StopOutMessage>());
            },
            services: s => s.AddSingleton(probe).AddTransient<StopInConsumer>().AddTransient<StopOutConsumer>(),
            minimumLogLevel: LogLevel.Information,
            cancellationToken: TestContext.Current.CancellationToken);

        // Arrange: publish all 20 messages BEFORE calling StopAsync, so they are genuinely "in flight"
        // (queued and/or being handled) at the moment shutdown begins — PublishAsync only writes to the
        // bus's own outgoing channel, so this completes long before any of the 20 are actually consumed.
        var publishes = new List<Task>(messageCount);
        for (int i = 0; i < messageCount; i++)
        {
            publishes.Add(host.Bus.PublishAsync(new StopInMessage(i), TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(publishes).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Act — StopAsync's graceful drain (BareWireBusControl.DrainBeforeCancellingConsumersAsync) must
        // let every in-flight "stop-in" handler run to completion, including its follow-up publish to
        // "stop-out", BEFORE consume loops are cancelled.
        await host.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert (evidence, not just the end state): every follow-up was delivered, and no handler ever
        // observed its own cancellation token cancelled — proof the drain ran to completion before
        // consume loops were cancelled, not merely that it happened to finish in time.
        probe.ReceivedOutIds.Should().BeEquivalentTo(Enumerable.Range(0, messageCount));
        probe.CancelledCount.Should().Be(0, "the graceful drain must complete before consume loops are cancelled");
    }

    [Fact]
    public async Task StopAsync_NonEmptyDeadLetterQueueWithoutConsumer_CompletesWellBeforeDrainTimeout()
    {
        const string dlqQueue = "stop-dlq";
        const int messageCount = 10;

        // No ReceiveEndpoint is declared for "stop-dlq" — it is declared directly on the topology so it
        // exists on the broker (routable via the default exchange) without ever gaining a consumer. A
        // queue nobody consumes from is, by design, skipped entirely by the graceful-drain quiescence
        // check (InMemoryTransportAdapter.IsDrained only waits on queues with HasActiveConsumer).
        await using InMemoryBusHost host = await InMemoryBusHost.StartAsync(
            transport: t =>
            {
                t.DefaultExchange("");
                t.QueueCapacity(50);
                t.SendTimeout(TimeSpan.FromSeconds(2));
                t.DrainTimeout(TimeSpan.FromSeconds(30));
                t.MapRoutingKey<DeadLetterMessage>(dlqQueue);
                t.ConfigureTopology(topology => topology.DeclareQueue(dlqQueue));
            },
            minimumLogLevel: LogLevel.Information,
            cancellationToken: TestContext.Current.CancellationToken);

        var publishes = new List<Task>(messageCount);
        for (int i = 0; i < messageCount; i++)
        {
            publishes.Add(host.Bus.PublishAsync(new DeadLetterMessage(i), TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(publishes).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await host.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        // A DrainTimeout of 30s configured but never actually waited out: "well before" is asserted as
        // an order-of-magnitude margin (3x), not a tight bound, to stay tolerant on a loaded CI runner.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "a queue with no active consumer must never extend graceful drain, regardless of DrainTimeout");
    }

    [Fact]
    public async Task DisposeAsync_PublishChannelHoldsMessagesForFullQueues_DoesNotWaitAndLogsDroppedPerQueue()
    {
        const string queueA = "stop-drop-a";
        const string queueB = "stop-drop-b";
        const int queueCapacity = 5;
        const int perQueueMessageCount = 100;
        var sendTimeout = TimeSpan.FromSeconds(1);
        var drainTimeout = TimeSpan.FromSeconds(1);
        var probe = new StopDropProbe();

        await using InMemoryBusHost host = await InMemoryBusHost.StartAsync(
            transport: t =>
            {
                t.DefaultExchange("");
                t.AutoDeclareEndpointQueues();
                t.QueueCapacity(queueCapacity);
                t.SendTimeout(sendTimeout);
                t.DrainTimeout(drainTimeout);
                t.MapRoutingKey<StopDropMessageA>(queueA);
                t.MapRoutingKey<StopDropMessageB>(queueB);
                t.ReceiveEndpoint(queueA, e => e.Consumer<StalledConsumerA, StopDropMessageA>());
                t.ReceiveEndpoint(queueB, e => e.Consumer<StalledConsumerB, StopDropMessageB>());
            },
            services: s => s.AddSingleton(probe).AddTransient<StalledConsumerA>().AddTransient<StalledConsumerB>(),
            minimumLogLevel: LogLevel.Information,
            cancellationToken: TestContext.Current.CancellationToken);

        // Arrange: both queues' consumers block forever on the first message they are ever handed (D5),
        // so both queues fill to capacity and stay full. Far more than fits (100 vs. a capacity of 5) is
        // published to each, so most of the 200 messages never even leave the bus's OWN outgoing channel
        // before the publisher loop's single-wait-per-batch budget (InMemorySender.cs) is spent.
        var publishes = new List<Task>(perQueueMessageCount * 2);
        for (int i = 0; i < perQueueMessageCount; i++)
        {
            publishes.Add(host.Bus.PublishAsync(new StopDropMessageA(i), TestContext.Current.CancellationToken));
            publishes.Add(host.Bus.PublishAsync(new StopDropMessageB(i), TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(publishes).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Deterministically reproduce PERF-1's exact scenario — the publisher loop actually PARKED
        // inside a SendTimeout wait against a full, still-active-consumer (non-latched) queue — instead
        // of hoping to land inside that up-to-1s window by timing alone. HasPendingSpaceWaiter is a
        // dedicated test hook on InMemoryQueue for exactly this.
        InMemoryQueue internalQueueA = GetQueueOrThrow(host, queueA);
        InMemoryQueue internalQueueB = GetQueueOrThrow(host, queueB);

        bool waiterObserved = await WaitUntilAsync(
            () => internalQueueA.HasPendingSpaceWaiter || internalQueueB.HasPendingSpaceWaiter,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        waiterObserved.Should().BeTrue(
            "the publisher loop must be observed mid-SendTimeout-wait against a full, non-latched queue " +
            "before shutdown begins, or this test would not exercise the scenario it claims to");

        // Act — measure StopAsync's own elapsed time (graceful drain bounded by the short DrainTimeout
        // above, then cancelling consumers) plus the container/adapter dispose that follows it.
        var stopwatch = Stopwatch.StartNew();

        // StopAsync alone — NOT DisposeAsync yet. ReceiveEndpointRunner settles a cancelled in-flight
        // delivery via SettleAsync(..., CancellationToken.None) (Requeue/dead-letter), so a cancelled
        // consume loop keeps resolving whatever it can reach — quickly, but not necessarily instantly —
        // before it finally observes its own cancellation and exits (HasActiveConsumer -> false for
        // both queues). Splitting StopAsync from container disposal lets this settle out completely
        // BEFORE anything is asserted about "what's left", instead of racing it against adapter Dispose.
        await host.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // With both consume loops now fully exited (no active consumer left on either queue), inject one
        // message straight through the still-undisposed transport adapter (bypassing IBus.PublishAsync,
        // which already throws ObjectDisposedException at this point since BareWireBus itself disposed as
        // part of StopAsync above). This message can never be delivered — nothing is left to dequeue it —
        // so it is exactly the kind of "arrived a moment too late" delivery
        // InMemoryTransportAdapter.Dispose's own docs describe as being dropped and reported the same way
        // as anything else abandoned in a queue's channel. Same observable effect as the scenario's own
        // framing (a message still addressed to a queue whose consumer has just been cancelled) with no
        // dependence on exactly how many redelivery cycles ReceiveEndpointRunner needed to get there.
        IMessageSerializer serializer = host.Services.GetRequiredService<IMessageSerializer>();
        await InjectUndeliverableMessageAsync(host, serializer, queueA, new StopDropMessageA(-1), TestContext.Current.CancellationToken);
        await InjectUndeliverableMessageAsync(host, serializer, queueB, new StopDropMessageB(-1), TestContext.Current.CancellationToken);

        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        // Explicit bound: drainTimeout (the graceful-drain budget) + up to two further SendTimeout waits
        // (one per queue, each queue's own "one wait per call" episode; see InMemorySender.cs) + a
        // generous margin for CI jitter — and, crucially, orders of magnitude below what waiting once per
        // rejected message would cost: perQueueMessageCount * sendTimeout = 100s per queue.
        TimeSpan explicitBound = drainTimeout + sendTimeout + sendTimeout + TimeSpan.FromSeconds(10);
        stopwatch.Elapsed.Should().BeLessThan(explicitBound);
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromTicks(sendTimeout.Ticks * perQueueMessageCount / 10),
            "DisposeAsync must not wait once per rejected/dropped message");

        // Assert — per-queue drop evidence AFTER DisposeAsync (the log/metric fire from the transport
        // ADAPTER's own Dispose, not from StopAsync): the messages that made it into each queue's own
        // channel (up to its capacity) but were never consumed are reported once, per queue, tagged
        // "drain_dropped" — never just a bare queue-name match, which a latch log would also satisfy.
        host.Telemetry
            .CounterTotal(InMemoryTransportMetrics.RejectedCounterName,
                (InMemoryTransportMetrics.ReasonTag, "drain_dropped"), (InMemoryTransportMetrics.QueueTag, queueA))
            .Should().BeGreaterThan(0);
        host.Telemetry
            .CounterTotal(InMemoryTransportMetrics.RejectedCounterName,
                (InMemoryTransportMetrics.ReasonTag, "drain_dropped"), (InMemoryTransportMetrics.QueueTag, queueB))
            .Should().BeGreaterThan(0);

        host.Telemetry.HasLog(LogLevel.Warning, $"undelivered message(s) on queue '{queueA}'").Should().BeTrue();
        host.Telemetry.HasLog(LogLevel.Warning, $"undelivered message(s) on queue '{queueB}'").Should().BeTrue();
    }

    /// <summary>
    /// Sends <paramref name="message"/> straight to the given queue through the transport adapter,
    /// bypassing <see cref="IBus.PublishAsync{T}(T, CancellationToken)"/> entirely (built the same way
    /// the bus itself would — see <c>BareWireBus.PublishAsync</c> — since <c>IBus.PublishAsync</c> throws
    /// <see cref="ObjectDisposedException"/> once the bus has already disposed).
    /// </summary>
    private static async Task InjectUndeliverableMessageAsync<T>(
        InMemoryBusHost host, IMessageSerializer serializer, string queueName, T message, CancellationToken cancellationToken)
        where T : class
    {
        Dictionary<string, string> headers = new()
        {
            ["BW-MessageType"] = typeof(T).Name,
            ["message-id"] = Guid.NewGuid().ToString(),
            ["BW-Exchange"] = string.Empty,
        };

        OutboundMessage outbound = MessagePipeline.ProcessOutboundAsync(message, serializer, queueName, headers, cancellationToken);
        await host.Adapter.SendBatchAsync([outbound], cancellationToken).ConfigureAwait(false);
    }

    private static InMemoryQueue GetQueueOrThrow(InMemoryBusHost host, string queueName)
    {
        if (!host.Adapter.Broker.TryGetQueue(queueName, out InMemoryQueue? queue))
        {
            throw new InvalidOperationException($"In-memory queue '{queueName}' was not found on the broker.");
        }

        return queue;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            while (!condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), linkedCts.Token).ConfigureAwait(false);
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

    private sealed record StopInMessage(int Id);

    private sealed record StopOutMessage(int Id);

    private sealed record DeadLetterMessage(int Id);

    private sealed record StopDropMessageA(int Id);

    private sealed record StopDropMessageB(int Id);

    // ── Probes (per-test singletons, injected into consumers — no static mutable state) ──────────────

    private sealed class FollowUpProbe
    {
        private readonly ConcurrentBag<int> _receivedOutIds = [];
        private long _cancelledCount;

        public IReadOnlyCollection<int> ReceivedOutIds => _receivedOutIds;

        public long CancelledCount => Interlocked.Read(ref _cancelledCount);

        public void RecordReceivedOut(int id) => _receivedOutIds.Add(id);

        public void RecordCancelled() => Interlocked.Increment(ref _cancelledCount);
    }

    private sealed class StopDropProbe
    {
        // Never completed — the two stalled consumers below unblock only when their own consume loop is
        // cancelled during shutdown (context.CancellationToken), exactly matching how a real, permanently
        // wedged handler would be released.
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // ── Consumers ──────────────────────────────────────────────────────────────────────────────────

    private sealed class StopInConsumer(FollowUpProbe probe) : IConsumer<StopInMessage>
    {
        public async Task ConsumeAsync(ConsumeContext<StopInMessage> context)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), context.CancellationToken).ConfigureAwait(false);
                await context.PublishAsync(new StopOutMessage(context.Message.Id), context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                probe.RecordCancelled();
                throw;
            }
        }
    }

    private sealed class StopOutConsumer(FollowUpProbe probe) : IConsumer<StopOutMessage>
    {
        public Task ConsumeAsync(ConsumeContext<StopOutMessage> context)
        {
            probe.RecordReceivedOut(context.Message.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class StalledConsumerA(StopDropProbe probe) : IConsumer<StopDropMessageA>
    {
        public async Task ConsumeAsync(ConsumeContext<StopDropMessageA> context) =>
            await probe.Gate.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
    }

    private sealed class StalledConsumerB(StopDropProbe probe) : IConsumer<StopDropMessageB>
    {
        public async Task ConsumeAsync(ConsumeContext<StopDropMessageB> context) =>
            await probe.Gate.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
    }
}
