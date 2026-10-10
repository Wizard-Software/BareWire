using Azure.Messaging.ServiceBus;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AzureServiceBus;
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

    private static (AzureServiceBusTransportAdapter Adapter, ServiceBusSessionReceiver Receiver) NewAdapter()
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

        return (new AzureServiceBusTransportAdapter(Options(sessions: true), new CapturingLogger(), client), receiver);
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
}
