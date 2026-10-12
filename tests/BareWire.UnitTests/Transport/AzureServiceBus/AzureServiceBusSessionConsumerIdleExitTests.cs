using Azure.Messaging.ServiceBus;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Configuration;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;
using static BareWire.UnitTests.Transport.AzureServiceBus.AzureServiceBusShutdownTestSupport;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

/// <summary>
/// Guards the idle-exit contract of the session consumer: when a session goes idle while the handler still
/// holds forwarded messages, the session receiver stays open (without receiving again) until those messages are
/// settled, the session lock ends, the lock is lost, or the consumer stops.
/// </summary>
public sealed class AzureServiceBusSessionConsumerIdleExitTests
{
    private const string SessionId = "session-1";

    private static FlowControlOptions Flow(int capacity = 100) => new() { InternalQueueCapacity = capacity };

    private static (AzureServiceBusTransportAdapter Adapter, ServiceBusSessionReceiver Receiver) NewAdapter(
        TimeSpan lockFor,
        TimeSpan? renewFor = null,
        FakeTimeProvider? fake = null)
    {
        var configurator = new AzureServiceBusConfigurator();
        configurator.ConnectionString(
            "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=dGVzdA==");
        configurator.UseSessions(maxConcurrentSessions: 1);
        configurator.MaxAutoLockRenewDuration(renewFor ?? TimeSpan.Zero);

        ServiceBusSessionReceiver receiver = Substitute.For<ServiceBusSessionReceiver>();
        receiver.SessionId.Returns(SessionId);
        receiver.SessionLockedUntil.Returns((fake?.GetUtcNow() ?? DateTimeOffset.UtcNow) + lockFor);

        ServiceBusClient client = Substitute.For<ServiceBusClient>();
        int accepts = 0;
        client
            .AcceptNextSessionAsync(
                QueueName, Arg.Any<ServiceBusSessionReceiverOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref accepts) == 1)
                {
                    return receiver;
                }

                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(2));
                return null!;
            });

        var adapter = new AzureServiceBusTransportAdapter(configurator.Build(), new CapturingLogger(), client);
        if (fake is not null)
        {
            adapter.SettlementTimeProvider = fake;
        }

        return (adapter, receiver);
    }

    private static bool Disposed(ServiceBusSessionReceiver receiver) =>
        receiver.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ServiceBusReceiver.DisposeAsync));

    private static bool Completed(ServiceBusSessionReceiver receiver) =>
        receiver.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ServiceBusReceiver.CompleteMessageAsync));

    private static bool Abandoned(ServiceBusSessionReceiver receiver) =>
        receiver.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ServiceBusReceiver.AbandonMessageAsync));

    [Fact]
    public async Task ConsumeAsync_SessionIdlesWhileHandlerHoldsMessage_KeepsReceiverOpenUntilSettled()
    {
        // Arrange
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) = NewAdapter(TimeSpan.FromMinutes(5));
        var idleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProgramReceive(receiver, [Batch("m", 1, SessionId)], idle: _ => idleGate.Task);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken)).Should().BeTrue();
        InboundMessage inFlight = enumerator.Current;

        // Act — the session goes idle while the handler holds the message.
        idleGate.SetResult();
        (await PollAsync(() => ReceiveCalls(receiver) >= 2, Guard)).Should().BeTrue();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Assert
        Disposed(receiver).Should().BeFalse("the idle session stays open while a forwarded message is unsettled");
        ReceiveCalls(receiver).Should().Be(2, "an idle session waiting for settlement must not receive again");
        await adapter.SettleAsync(SettlementAction.Ack, inFlight, TestContext.Current.CancellationToken);
        Completed(receiver).Should().BeTrue();
        (await PollAsync(() => Disposed(receiver), Guard)).Should().BeTrue("the session is released once settled");

        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task ConsumeAsync_SessionIdlesAndMessageNeverSettled_ReleasesAtSessionLockBound()
    {
        // Arrange — the lock ends in 500 ms and renewal is off, so that is the bound.
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) = NewAdapter(TimeSpan.FromMilliseconds(500));
        var idleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProgramReceive(receiver, [Batch("m", 1, SessionId)], idle: _ => idleGate.Task);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken)).Should().BeTrue();

        // Act
        idleGate.SetResult();

        // Assert — never settled: the session is released after the bound, not held forever.
        (await PollAsync(() => Disposed(receiver), Guard)).Should().BeTrue();

        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task ConsumeAsync_SessionIdlesWithNothingInFlight_ReleasesWithoutWaiting()
    {
        // Arrange — the message is settled before the session goes idle.
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) = NewAdapter(TimeSpan.FromMinutes(5));
        var idleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProgramReceive(receiver, [Batch("m", 1, SessionId)], idle: _ => idleGate.Task);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken)).Should().BeTrue();
        await adapter.SettleAsync(SettlementAction.Ack, enumerator.Current, TestContext.Current.CancellationToken);

        // Act
        idleGate.SetResult();

        // Assert — well inside the 5-minute lock bound.
        (await PollAsync(() => Disposed(receiver), TimeSpan.FromSeconds(2))).Should().BeTrue();

        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task ConsumeAsync_ConsumerStopsWhileIdleSessionWaits_KeepsReceiverUntilTeardown()
    {
        // Arrange — idle wait in progress, then the runner cancels the consume token.
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) = NewAdapter(TimeSpan.FromMinutes(5));
        var idleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProgramReceive(receiver, [Batch("m", 1, SessionId)], idle: _ => idleGate.Task);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken)).Should().BeTrue();
        InboundMessage inFlight = enumerator.Current;
        idleGate.SetResult();
        (await PollAsync(() => ReceiveCalls(receiver) >= 2, Guard)).Should().BeTrue();

        // Act
        await cts.CancelAsync();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Assert — hand-over to the existing stop path: still open, the late Ack still lands.
        Disposed(receiver).Should().BeFalse();
        await adapter.SettleAsync(SettlementAction.Ack, inFlight, TestContext.Current.CancellationToken);
        Completed(receiver).Should().BeTrue();
        await enumerator.DisposeAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);
        Disposed(receiver).Should().BeTrue();
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task ConsumeAsync_RequeueWhileIdleSessionWaits_AbandonsOnOpenReceiverThenReleases()
    {
        // Arrange
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) = NewAdapter(TimeSpan.FromMinutes(5));
        var idleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProgramReceive(receiver, [Batch("m", 1, SessionId)], idle: _ => idleGate.Task);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken)).Should().BeTrue();
        InboundMessage inFlight = enumerator.Current;
        idleGate.SetResult();
        (await PollAsync(() => ReceiveCalls(receiver) >= 2, Guard)).Should().BeTrue();

        // Act
        await adapter.SettleAsync(SettlementAction.Requeue, inFlight, TestContext.Current.CancellationToken);

        // Assert — the abandon reached the still-open receiver, then the session was released.
        Abandoned(receiver).Should().BeTrue();
        (await PollAsync(() => Disposed(receiver), Guard)).Should().BeTrue();

        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task ConsumeAsync_RenewalLosesLockWhileIdleSessionWaits_ReleasesBeforeLockBound()
    {
        // Arrange — renewal on (fake clock); the lock still looks valid for 5 minutes, but the first renew
        // reports SessionLockLost.
        var fake = new FakeTimeProvider();
        (AzureServiceBusTransportAdapter adapter, ServiceBusSessionReceiver receiver) =
            NewAdapter(TimeSpan.FromMinutes(5), renewFor: TimeSpan.FromMinutes(5), fake: fake);
        receiver.RenewSessionLockAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ServiceBusException("lost", ServiceBusFailureReason.SessionLockLost)));
        var idleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProgramReceive(receiver, [Batch("m", 1, SessionId)], idle: _ => idleGate.Task);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = adapter.ConsumeAsync(QueueName, Flow(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken)).Should().BeTrue();
        idleGate.SetResult();
        (await PollAsync(() => ReceiveCalls(receiver) >= 2, Guard)).Should().BeTrue();

        // Act — advance past the first renew (about 2.4 min with a 5-minute lock) but stay before the bound.
        for (int i = 0; i < 8 && !Disposed(receiver); i++)
        {
            fake.Advance(TimeSpan.FromSeconds(30));
            await PollAsync(() => Disposed(receiver), TimeSpan.FromMilliseconds(200));
        }

        // Assert — released on lock loss, while fake time is still before SessionLockedUntil.
        Disposed(receiver).Should().BeTrue("a lost session lock ends the idle wait at once");
        await receiver.Received().RenewSessionLockAsync(Arg.Any<CancellationToken>());

        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);
        await adapter.DisposeAsync();
    }
}
