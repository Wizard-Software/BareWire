using Azure.Messaging.ServiceBus;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;
using static BareWire.UnitTests.Transport.AzureServiceBus.AzureServiceBusShutdownTestSupport;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

/// <summary>
/// Guards the session consumer shutdown contract: the consume token stops accepting sessions and receiving,
/// but the session receivers stay open until the caller has settled its in-flight messages (the enumerator is
/// disposed); a requeue while stopping waits for the receive loops; messages received after cancellation are
/// abandoned.
/// </summary>
public sealed class AzureServiceBusSessionConsumerShutdownTests
{
    private const string SessionId = "session-1";

    private static FlowControlOptions Flow(int capacity = 100) => new() { InternalQueueCapacity = capacity };

    // The settlement wait is off (zero budget) unless a test opts in: tests that take a message and never settle
    // it would otherwise sit out the full wait when the consumer stops.
    private static (AzureServiceBusTransportAdapter Adapter, ServiceBusSessionReceiver Receiver) NewAdapter(
        CapturingLogger? logger = null,
        TimeSpan? settlementBudget = null,
        TimeProvider? timeProvider = null)
    {
        ServiceBusSessionReceiver receiver = Substitute.For<ServiceBusSessionReceiver>();
        receiver.SessionId.Returns(SessionId);

        ServiceBusClient client = Substitute.For<ServiceBusClient>();
        int accepts = 0;
        client
            .AcceptNextSessionAsync(
                QueueName, Arg.Any<ServiceBusSessionReceiverOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                // The first call hands out the session; later ones behave like an empty queue.
                if (Interlocked.Increment(ref accepts) == 1)
                {
                    return receiver;
                }

                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(2));
                return null!;
            });

        var adapter = new AzureServiceBusTransportAdapter(
            Options(sessions: true), logger ?? new CapturingLogger(), client)
        {
            SettlementBudget = settlementBudget ?? TimeSpan.Zero,
        };
        if (timeProvider is not null)
        {
            adapter.SettlementTimeProvider = timeProvider;
        }

        return (adapter, receiver);
    }

    // The observable end of a stopping enumerator: MoveNextAsync ends with OperationCanceledException once the
    // generator's finally (the consumer stop) has run.
    private static Task Observed(Task end) => end.ContinueWith(static _ => { }, TaskScheduler.Default);

    private static void MarkOnDispose(ServiceBusSessionReceiver receiver, CapturingLogger logger)
    {
        // NSubstitute configures the call by "awaiting" the substitute's return value; the ValueTask is never consumed.
#pragma warning disable CA2012
        receiver.DisposeAsync().Returns(_ =>
        {
            logger.Mark("close");
            return ValueTask.CompletedTask;
        });
#pragma warning restore CA2012
    }

    private static bool Disposed(ServiceBusSessionReceiver receiver) =>
        receiver.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ServiceBusReceiver.DisposeAsync));

    private static bool Completed(ServiceBusSessionReceiver receiver) =>
        receiver.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ServiceBusReceiver.CompleteMessageAsync));

    [Fact]
    public async Task ConsumeAsync_InFlightMessageSettledAfterTokenCancelled_ReachesStillOpenSessionReceiver()
    {
        // Arrange
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) = NewAdapter();
        var receiveEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProgramReceive(receiver, [Batch("m", 1, SessionId)], async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                receiveEnded.TrySetResult();
            }
        });
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        InboundMessage inFlight = enumerator.Current;

        // Act — the runner cancels the token while the handler still holds the message.
        await cts.CancelAsync();
        await receiveEnded.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Assert — receiving stopped, but the session receiver must not have been closed yet ...
        Disposed(receiver).Should().BeFalse(
            "the session receiver stays open until the caller has settled its in-flight messages");

        // ... so the late Ack finds its registry entry and reaches the receiver.
        await adapter.SettleAsync(SettlementAction.Ack, inFlight, TestContext.Current.CancellationToken);
        Completed(receiver).Should().BeTrue();

        // Disposing the enumerator is the "generator finally": now the session is torn down.
        await enumerator.DisposeAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);
        Disposed(receiver).Should().BeTrue("StopAsync waits for the per-session task, which disposes the receiver");

        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_RequeueWhileStoppingSessionConsumer_AbandonsOnlyAfterReceiveLoopStopped()
    {
        // Arrange — the receive call needs a moment to observe the cancellation.
        var releaseReceive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) = NewAdapter();
        ProgramReceive(receiver, [Batch("m", 1, SessionId)], async ct =>
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

        // Act
        await cts.CancelAsync();
        Task settle = adapter.SettleAsync(SettlementAction.Requeue, inFlight, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        settle.IsCompleted.Should().BeFalse("a requeue must wait until no session receive call is in flight");
        AbandonedIds(receiver).Should().BeEmpty();

        releaseReceive.SetResult();
        await settle.WaitAsync(Guard, TestContext.Current.CancellationToken);
        AbandonedIds(receiver).Should().ContainSingle().Which.Should().Be("m1");
        Disposed(receiver).Should().BeFalse("the abandon must reach a receiver that is still open");

        await enumerator.DisposeAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task ConsumeAsync_CancelledWhileSessionMessageWaitsToWrite_AbandonsWaitingMessageAndRestOfBatch()
    {
        // Arrange — capacity 1 everywhere. m1 is taken by the caller, m2 fills the output channel, m3 is held by
        // the forwarding task, m4 sits in the session channel, m5 waits in WaitToWriteAsync and m6, m7 were
        // never registered.
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) = NewAdapter();
        ProgramReceive(receiver, [Batch("m", 7, SessionId)]);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(capacity: 1), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        (await PollAsync(() => ReceiveCalls(receiver) >= 1, Guard)).Should().BeTrue();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Act
        await cts.CancelAsync();

        // Assert — only messages that were never handed to a buffer are abandoned one by one; the buffered
        // ones are released when the session receiver closes.
        (await PollAsync(() => AbandonedIds(receiver).Count >= 3, Guard)).Should().BeTrue();
        AbandonedIds(receiver).Should().BeEquivalentTo(["m5", "m6", "m7"]);

        await enumerator.DisposeAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);
        Disposed(receiver).Should().BeTrue();
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task ConsumeAsync_EnumeratorDisposedWithoutCancellation_TearsSessionDown()
    {
        // Arrange — the consume token is never cancelled (runner failed outside cancellation).
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) = NewAdapter();
        ProgramReceive(receiver, [Batch("m", 1, SessionId)]);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();

        // Act
        await enumerator.DisposeAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        cts.IsCancellationRequested.Should().BeFalse();
        Disposed(receiver).Should().BeTrue();

        await adapter.DisposeAsync();
    }

    // ── Settlement wait before the session receivers are released ─────────────

    [Fact]
    public async Task StopAsync_InFlightMessageSettledDuringWait_CompletesBeforeSessionClose()
    {
        // Arrange
        var logger = new CapturingLogger();
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) =
            NewAdapter(logger, AzureServiceBusSettlementDrain.Budget);
        MarkOnDispose(receiver, logger);
        receiver
            .CompleteMessageAsync(Arg.Any<ServiceBusReceivedMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                logger.Mark("complete");
                return Task.CompletedTask;
            });
        ProgramReceive(receiver, [Batch("m", 1, SessionId)]);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        InboundMessage message = enumerator.Current;

        // Act — the runner stops while a lane still holds the message, then settles it.
        await cts.CancelAsync();
        Task end = enumerator.MoveNextAsync().AsTask();
        await Task.WhenAny(end, logger.WaitStarted.Task).WaitAsync(Guard, TestContext.Current.CancellationToken);
        Func<Task> settle = () => adapter.SettleAsync(SettlementAction.Ack, message, CancellationToken.None);
        await settle.Should().NotThrowAsync("the registry entry must still exist while the consumer waits");
        await Observed(end).WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        logger.Timeline.Should().ContainInOrder("complete", "close");

        await enumerator.DisposeAsync();
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_SettlementInProgress_ClosesSessionOnlyAfterSdkCallReturns()
    {
        // Arrange — CompleteMessageAsync stays inside the SDK call until the test releases it.
        var logger = new CapturingLogger();
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) =
            NewAdapter(logger, AzureServiceBusSettlementDrain.Budget);
        var completeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver
            .CompleteMessageAsync(Arg.Any<ServiceBusReceivedMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                completeEntered.TrySetResult();
                return completeGate.Task;
            });
        ProgramReceive(receiver, [Batch("m", 1, SessionId)]);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();

        // Act — the entry is evicted (nothing in flight) while the settlement is still inside the SDK call.
        Task settle = adapter.SettleAsync(SettlementAction.Ack, enumerator.Current, CancellationToken.None);
        await completeEntered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        Task end = enumerator.MoveNextAsync().AsTask();
        await Task.WhenAny(end, logger.WaitStarted.Task).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken); // give a premature teardown time to show

        // Assert — the session receiver is released only after the SDK call returned.
        Disposed(receiver).Should().BeFalse();
        completeGate.SetResult();
        await settle.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await Observed(end).WaitAsync(Guard, TestContext.Current.CancellationToken);
        Disposed(receiver).Should().BeTrue();

        await enumerator.DisposeAsync();
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_InFlightNeverSettled_ReleasesAfterBudget()
    {
        // Arrange
        var fake = new FakeTimeProvider();
        var logger = new CapturingLogger();
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) =
            NewAdapter(logger, AzureServiceBusSettlementDrain.Budget, fake);
        ProgramReceive(receiver, [Batch("m", 1, SessionId)]);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();

        // Act — nobody settles the message.
        await cts.CancelAsync();
        Task end = enumerator.MoveNextAsync().AsTask();
        Task endObserved = Observed(end);
        await Task.WhenAny(endObserved, logger.WaitStarted.Task).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await FakeTimePump.AdvanceUntilCompletedAsync(fake, endObserved, AzureServiceBusSettlementDrain.Budget, Guard);

        // Assert
        logger.Entries.Should().ContainSingle(e => e.EventName == "LogSettlementBudgetElapsed")
            .Which.Level.Should().Be(LogLevel.Warning);
        Disposed(receiver).Should().BeTrue();

        await enumerator.DisposeAsync();
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_NoInFlight_ReleasesWithoutWaiting()
    {
        // Arrange — fake time that is never advanced: any wait would hang.
        var fake = new FakeTimeProvider();
        var logger = new CapturingLogger();
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) =
            NewAdapter(logger, AzureServiceBusSettlementDrain.Budget, fake);
        ProgramReceive(receiver, [Batch("m", 1, SessionId)]);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        await adapter.SettleAsync(SettlementAction.Ack, enumerator.Current, CancellationToken.None);

        // Act
        await cts.CancelAsync();
        Task end = enumerator.MoveNextAsync().AsTask();
        await Observed(end).WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        logger.Entries.Should().NotContain(e => e.EventName == "LogAwaitingSettlements");
        Disposed(receiver).Should().BeTrue();

        await enumerator.DisposeAsync();
        await adapter.DisposeAsync();
    }
}
