using System.Diagnostics;
using System.Threading.Channels;
using Azure.Messaging.ServiceBus;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Internal;
using NSubstitute;
using Xunit;
using static BareWire.UnitTests.Transport.AzureServiceBus.AzureServiceBusShutdownTestSupport;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

/// <summary>
/// Guards the non-session consumer shutdown contract: receiving stops as soon as the consume token is
/// cancelled, a requeue while stopping waits for the receive loop, and every message that was received but
/// never handed to the caller is abandoned (within one shared time budget) and evicted from the registry.
/// </summary>
public sealed class AzureServiceBusConsumerShutdownTests
{
    private static FlowControlOptions Flow(int capacity = 100) => new() { InternalQueueCapacity = capacity };

    private static Channel<InboundMessage> NewChannel(int capacity = 100) =>
        Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false,
        });

    private static AzureServiceBusConsumer NewConsumer(
        ServiceBusReceiver receiver,
        AzureServiceBusConsumerRegistry registry,
        Channel<InboundMessage> channel,
        string consumerId = "c1",
        TimeSpan? budget = null,
        CapturingLogger? logger = null)
    {
        var consumer = new AzureServiceBusConsumer(
            receiver, channel, registry, consumerId, QueueName, logger ?? new CapturingLogger(), budget);
        registry.Register(consumerId, consumer);
        return consumer;
    }

    private static (AzureServiceBusTransportAdapter Adapter, ServiceBusReceiver Receiver) NewAdapter(
        CapturingLogger? logger = null)
    {
        ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
        ServiceBusClient client = Substitute.For<ServiceBusClient>();
        client.CreateReceiver(QueueName, Arg.Any<ServiceBusReceiverOptions>()).Returns(receiver);
        return (new AzureServiceBusTransportAdapter(Options(), logger ?? new CapturingLogger(), client), receiver);
    }

    // ── Receive loop stops on the consume token ───────────────────────────────

    [Fact]
    public async Task RunLoop_ConsumeTokenCancelled_StopsReceivingBeforeStopAsync()
    {
        // Arrange
        ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
        ProgramReceive(receiver, [], async ct =>
        {
            // A short poll that returns no messages, so the loop would spin forever if it ignored the token.
            await Task.Delay(5, ct);
        });
        var consumer = NewConsumer(receiver, new AzureServiceBusConsumerRegistry(), NewChannel());
        using var consumeCts = new CancellationTokenSource();
        consumer.StartLoop(consumeCts.Token);
        (await PollAsync(() => ReceiveCalls(receiver) > 2, Guard)).Should().BeTrue();

        // Act — cancel the consume token; StopAsync is NOT called.
        await consumeCts.CancelAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        int callsAfterCancel = ReceiveCalls(receiver);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        ReceiveCalls(receiver).Should().Be(callsAfterCancel, "receiving must stop when the consume token is cancelled");
        consumer.IsStopRequested.Should().BeTrue();

        await consumer.DisposeAsync();
    }

    [Fact]
    public async Task ConsumeAsync_TokenCancelledWithBufferedMessages_YieldsNoFurtherMessage()
    {
        // Arrange
        (AzureServiceBusTransportAdapter adapter, ServiceBusReceiver receiver) = NewAdapter();
        ProgramReceive(receiver, [Batch("m", 5)]);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        (await PollAsync(() => ReceiveCalls(receiver) >= 1, Guard)).Should().BeTrue();
        await Task.Delay(100, TestContext.Current.CancellationToken); // let the rest of the batch reach the buffer

        // Act
        await cts.CancelAsync();
        Func<Task> next = async () => await enumerator.MoveNextAsync();

        // Assert — the buffered messages are not handed out after cancellation.
        await next.Should().ThrowAsync<OperationCanceledException>();

        await enumerator.DisposeAsync();
        await adapter.DisposeAsync();
    }

    // ── Requeue while stopping ────────────────────────────────────────────────

    [Fact]
    public async Task SettleAsync_RequeueWhileStopping_AbandonsOnlyAfterReceiveLoopStopped()
    {
        // Arrange — the receive call needs a moment to observe the cancellation.
        var releaseReceive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (AzureServiceBusTransportAdapter adapter, ServiceBusReceiver receiver) = NewAdapter();
        ProgramReceive(receiver, [Batch("m", 1)], async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                await releaseReceive.Task;
                throw;
            }
        });
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        InboundMessage inFlight = enumerator.Current;
        (await PollAsync(() => ReceiveCalls(receiver) >= 2, Guard)).Should().BeTrue();

        // Act — the runner cancels, then settles the in-flight message with Requeue.
        await cts.CancelAsync();
        Task settle = adapter.SettleAsync(SettlementAction.Requeue, inFlight, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert — still waiting for the receive loop: nothing abandoned yet.
        settle.IsCompleted.Should().BeFalse("a requeue must wait until no receive call is in flight");
        AbandonedIds(receiver).Should().BeEmpty();

        releaseReceive.SetResult();
        await settle.WaitAsync(Guard, TestContext.Current.CancellationToken);
        AbandonedIds(receiver).Should().ContainSingle().Which.Should().Be("m1");

        await enumerator.DisposeAsync();
        await adapter.DisposeAsync();
    }

    // ── Draining the buffer ───────────────────────────────────────────────────

    [Fact]
    public async Task StopAsync_WithBufferedMessages_AbandonsEachAndEvictsRegistry()
    {
        // Arrange
        ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
        ProgramReceive(receiver, [Batch("m", 4)]);
        var registry = new AzureServiceBusConsumerRegistry();
        Channel<InboundMessage> channel = NewChannel();
        var consumer = NewConsumer(receiver, registry, channel);
        consumer.StartLoop(CancellationToken.None);
        (await PollAsync(() => channel.Reader.Count == 4, Guard)).Should().BeTrue();
        registry.InFlightCount("c1").Should().Be(4);

        // Act
        await consumer.StopAsync().WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        AbandonedIds(receiver).Should().BeEquivalentTo(["m1", "m2", "m3", "m4"]);
        registry.InFlightCount("c1").Should().Be(0);
        registry.TryGetReceiveControl("c1", out _).Should().BeFalse();
        await receiver.Received(1).CloseAsync(Arg.Any<CancellationToken>());

        await consumer.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_CalledRepeatedly_ClosesReceiverOnce()
    {
        // Arrange
        ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
        ProgramReceive(receiver, []);
        var consumer = NewConsumer(receiver, new AzureServiceBusConsumerRegistry(), NewChannel());
        consumer.StartLoop(CancellationToken.None);

        // Act — the enumerator's finally and DisposeAsync both stop the consumer.
        Task first = consumer.StopAsync();
        Task second = consumer.StopAsync();
        await Task.WhenAll(first, second).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await consumer.DisposeAsync();

        // Assert
        second.Should().BeSameAs(first, "StopAsync is single-flight");
        await receiver.Received(1).CloseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_AbandonFails_SwitchesToDisposeOnlyAndDoesNotThrow()
    {
        // Arrange — 40 buffered messages, every Abandon fails with a message that must never reach the logs.
        ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
        receiver
            .AbandonMessageAsync(
                Arg.Any<ServiceBusReceivedMessage>(),
                Arg.Any<IDictionary<string, object>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(
                new ServiceBusException(SecretLockText, ServiceBusFailureReason.MessageLockLost)));
        ProgramReceive(receiver, [Batch("a", 10), Batch("b", 10), Batch("c", 10), Batch("d", 10)]);
        var registry = new AzureServiceBusConsumerRegistry();
        Channel<InboundMessage> channel = NewChannel(40);
        var logger = new CapturingLogger();
        var consumer = NewConsumer(receiver, registry, channel, logger: logger);
        consumer.StartLoop(CancellationToken.None);
        (await PollAsync(() => channel.Reader.Count == 40, Guard)).Should().BeTrue();

        // Act
        Func<Task> stop = () => consumer.StopAsync();
        await stop.Should().NotThrowAsync();

        // Assert
        AbandonedIds(receiver).Count.Should().BeLessThan(
            40, "after the first failure the remaining messages are only disposed");
        registry.InFlightCount("c1").Should().Be(0, "every message is evicted even in dispose-only mode");
        await receiver.Received(1).CloseAsync(Arg.Any<CancellationToken>());
        logger.Warnings.Should().ContainSingle(w => w.Contains("release failed", StringComparison.Ordinal));
        logger.AllText.Should().NotContain(SecretLockText, "exception messages can echo lock tokens");

        await consumer.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_SlowBroker_FinishesWithinBudgetAndLogsExhaustion()
    {
        // Arrange — Abandon never returns on its own.
        ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
        receiver
            .AbandonMessageAsync(
                Arg.Any<ServiceBusReceivedMessage>(),
                Arg.Any<IDictionary<string, object>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(2)));
        ProgramReceive(receiver, [Batch("m", 8)]);
        var registry = new AzureServiceBusConsumerRegistry();
        Channel<InboundMessage> channel = NewChannel();
        var logger = new CapturingLogger();
        var consumer = NewConsumer(receiver, registry, channel, budget: TimeSpan.FromMilliseconds(300), logger: logger);
        consumer.StartLoop(CancellationToken.None);
        (await PollAsync(() => channel.Reader.Count == 8, Guard)).Should().BeTrue();

        // Act
        var stopwatch = Stopwatch.StartNew();
        await consumer.StopAsync().WaitAsync(Guard, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        // Assert — one shared budget for the whole drain, not one per call.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        registry.InFlightCount("c1").Should().Be(0);
        await receiver.Received(1).CloseAsync(Arg.Any<CancellationToken>());
        logger.Warnings.Should().ContainSingle(w => w.Contains("budget exhausted", StringComparison.Ordinal));

        await consumer.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_ManyBufferedMessages_AbandonsWithBoundedParallelism()
    {
        // Arrange — track how many Abandon calls are in flight at once.
        int current = 0;
        int peak = 0;
        ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
        receiver
            .AbandonMessageAsync(
                Arg.Any<ServiceBusReceivedMessage>(),
                Arg.Any<IDictionary<string, object>>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                int now = Interlocked.Increment(ref current);
                int seen;
                while ((seen = Volatile.Read(ref peak)) < now &&
                       Interlocked.CompareExchange(ref peak, now, seen) != seen)
                {
                }

                await Task.Delay(20, CancellationToken.None);
                Interlocked.Decrement(ref current);
            });
        ProgramReceive(receiver, [Batch("a", 10), Batch("b", 10), Batch("c", 10), Batch("d", 10)]);
        Channel<InboundMessage> channel = NewChannel(40);
        var consumer = NewConsumer(receiver, new AzureServiceBusConsumerRegistry(), channel);
        consumer.StartLoop(CancellationToken.None);
        (await PollAsync(() => channel.Reader.Count == 40, Guard)).Should().BeTrue();

        // Act
        await consumer.StopAsync().WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        AbandonedIds(receiver).Should().HaveCount(40);
        peak.Should().BeLessThanOrEqualTo(AzureServiceBusShutdownDrain.MaxParallelAbandons);

        await consumer.DisposeAsync();
    }

    // ── Rest of a batch received after cancellation ───────────────────────────

    [Fact]
    public async Task RunLoop_CancelledWhileWaitingToWrite_AbandonsWaitingMessageAndRestOfBatch()
    {
        // Arrange — capacity 1: m1 is buffered, m2 waits in WaitToWriteAsync, m3..m5 are not even registered.
        ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
        ProgramReceive(receiver, [Batch("m", 5)]);
        var registry = new AzureServiceBusConsumerRegistry();
        Channel<InboundMessage> channel = NewChannel(1);
        var consumer = NewConsumer(receiver, registry, channel);
        using var consumeCts = new CancellationTokenSource();
        consumer.StartLoop(consumeCts.Token);
        (await PollAsync(() => registry.InFlightCount("c1") == 2, Guard)).Should().BeTrue();

        // Act
        await consumeCts.CancelAsync();
        (await PollAsync(() => AbandonedIds(receiver).Count == 4, Guard)).Should().BeTrue();

        // Assert — the waiting message and the unregistered rest go back at once; the buffered one is
        // released by the drain when the consumer stops.
        AbandonedIds(receiver).Should().BeEquivalentTo(["m2", "m3", "m4", "m5"]);
        registry.InFlightCount("c1").Should().Be(1);

        await consumer.StopAsync().WaitAsync(Guard, TestContext.Current.CancellationToken);
        AbandonedIds(receiver).Should().BeEquivalentTo(["m1", "m2", "m3", "m4", "m5"]);
        registry.InFlightCount("c1").Should().Be(0);

        await consumer.DisposeAsync();
    }

    // ── Registry hygiene ──────────────────────────────────────────────────────

    [Fact]
    public async Task StartAndStop_RepeatedCycles_LeaveNoRegistryEntriesBehind()
    {
        // Arrange
        var registry = new AzureServiceBusConsumerRegistry();

        for (int cycle = 0; cycle < 25; cycle++)
        {
            ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
            ProgramReceive(receiver, [Batch($"c{cycle}-", 3)]);
            string id = $"consumer-{cycle}";
            Channel<InboundMessage> channel = NewChannel();
            var consumer = NewConsumer(receiver, registry, channel, id);
            consumer.StartLoop(CancellationToken.None);
            (await PollAsync(() => channel.Reader.Count == 3, Guard)).Should().BeTrue();

            // Act — one message is taken and settled, the rest stays buffered.
            channel.Reader.TryRead(out InboundMessage? taken).Should().BeTrue();
            var entry = registry.TryEvictMessage(id, taken!.DeliveryTag);
            entry.Should().NotBeNull();

            await consumer.StopAsync().WaitAsync(Guard, TestContext.Current.CancellationToken);
            await consumer.DisposeAsync();

            // Assert
            registry.InFlightCount(id).Should().Be(0);
            registry.TryGetReceiveControl(id, out _).Should().BeFalse();
        }

        registry.AllConsumers().Should().BeEmpty();
    }
}
