using System.Threading.Channels;
using AwesomeAssertions;
using Azure.Messaging.ServiceBus;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Configuration;
using BareWire.Transport.AzureServiceBus.Internal;
using NSubstitute;
using Xunit;
using static BareWire.UnitTests.Transport.AzureServiceBus.AzureServiceBusShutdownTestSupport;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

/// <summary>
/// Guards the hand-off of several session drain tasks into the one shared output buffer: when the buffer is
/// full and more than one session is waiting for room, every waiting writer must eventually deliver its
/// message (in order within its session) instead of losing a race for a freed slot.
/// </summary>
public sealed class AzureServiceBusSessionConsumerFullChannelTests
{
    [Fact]
    public async Task DrainSessionChannel_TwoSessionsWaitOnFullOutputChannel_DeliversEveryMessageInOrder()
    {
        // Arrange — two sessions with two messages each share an output channel of capacity 1.
        var configurator = new AzureServiceBusConfigurator();
        configurator.ConnectionString(
            "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=dGVzdA==");
        configurator.UseSessions(maxConcurrentSessions: 2);
        configurator.MaxAutoLockRenewDuration(TimeSpan.Zero);
        AzureServiceBusTransportOptions options = configurator.Build();

        ServiceBusSessionReceiver receiverA = NewSessionReceiver("A");
        ServiceBusSessionReceiver receiverB = NewSessionReceiver("B");

        ServiceBusClient client = Substitute.For<ServiceBusClient>();
        var sessions = new Queue<ServiceBusSessionReceiver>([receiverA, receiverB]);
        client
            .AcceptNextSessionAsync(
                QueueName, Arg.Any<ServiceBusSessionReceiverOptions>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                ServiceBusSessionReceiver? next;
                lock (sessions)
                {
                    sessions.TryDequeue(out next);
                }

                if (next is not null)
                {
                    return next;
                }

                // No further session: behave like an empty queue until the consumer stops.
                await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(2));
                return null!;
            });

        var output = new ObservedOutputChannel();
        var registry = new AzureServiceBusConsumerRegistry();
        const string consumerId = "full-channel-consumer";
        var consumer = new AzureServiceBusSessionConsumer(
            client,
            QueueName,
            options,
            registry,
            consumerId,
            output,
            new FlowControlOptions { InternalQueueCapacity = 100 },
            new CapturingLogger(),
            settlementBudget: TimeSpan.Zero);
        registry.RegisterSession(consumerId, consumer);

        using var cts = new CancellationTokenSource();
        var read = new List<InboundMessage>();

        try
        {
            // Act — start the consumer; both drain tasks end up waiting on the full output channel
            // (one message buffered, two writers queued).
            consumer.StartLoop(cts.Token);
            (await PollAsync(() => output.BlockedWriters == 2 && output.Inner.Reader.Count == 1, Guard))
                .Should().BeTrue("both session drain tasks must wait for room in the full output channel");

            int tryWritesBefore = output.TryWriteCalls;
            int completedWritesBefore = output.CompletedWriteAsyncCalls;

            // Free exactly one slot.
            output.Inner.Reader.TryRead(out InboundMessage? first).Should().BeTrue();
            read.Add(first!);

            // Wait until the contest for the freed slot is decided: either both woken writers tried
            // TryWrite (one of them necessarily loses), or one WriteAsync completed while the other stays queued.
            (await PollAsync(
                () => output.TryWriteCalls - tryWritesBefore >= 2
                    || (output.CompletedWriteAsyncCalls - completedWritesBefore >= 1 && output.BlockedWriters >= 1),
                Guard))
                .Should().BeTrue("the freed slot must be taken by a waiting writer");

            while (read.Count < 4)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.Current.CancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                InboundMessage next;
                try
                {
                    next = await output.Inner.Reader.ReadAsync(readTimeout.Token);
                }
                catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
                {
                    break;
                }

                read.Add(next);
            }

            // Assert — all asserted before the consumer is stopped, so shutdown draining cannot mask a loss.
            string[] ids = [.. read.Select(m => m.MessageId)];
            ids.Should().BeEquivalentTo(["A1", "A2", "B1", "B2"], "no waiting writer may lose its message");
            read.Select(m => m.DeliveryTag).Distinct().Should().HaveCount(4);
            ids.ToList().IndexOf("A1").Should().BeLessThan(ids.ToList().IndexOf("A2"));
            ids.ToList().IndexOf("B1").Should().BeLessThan(ids.ToList().IndexOf("B2"));
            output.FailedTryWrites.Should().Be(0, "a lost race for a freed slot must not be possible");
            AbandonedIds(receiverA).Should().BeEmpty();
            AbandonedIds(receiverB).Should().BeEmpty();
        }
        finally
        {
            await cts.CancelAsync();
            await consumer.StopAsync().WaitAsync(Guard, TestContext.Current.CancellationToken);
            await consumer.DisposeAsync().AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);
            foreach (InboundMessage message in read)
            {
                message.Dispose();
            }
        }
    }

    private static ServiceBusSessionReceiver NewSessionReceiver(string sessionId)
    {
        ServiceBusSessionReceiver receiver = Substitute.For<ServiceBusSessionReceiver>();
        receiver.SessionId.Returns(sessionId);

        // One batch of two messages; every later receive waits for cancellation (no idle session exit).
        ProgramReceive(
            receiver,
            [[Message($"{sessionId}1", sessionId), Message($"{sessionId}2", sessionId)]]);
        return receiver;
    }

    /// <summary>A capacity-1, Wait-mode shared channel whose writer records how it is used.</summary>
    private sealed class ObservedOutputChannel : Channel<InboundMessage>
    {
        public ObservedOutputChannel()
        {
            Inner = Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = false,
            });
            var writer = new ObservedWriter(Inner.Writer);
            Observed = writer;
            Reader = Inner.Reader;
            Writer = writer;
        }

        public Channel<InboundMessage> Inner { get; }

        private ObservedWriter Observed { get; }

        /// <summary>Gets the writes that did not complete synchronously and are still waiting.</summary>
        public int BlockedWriters => Volatile.Read(ref Observed.Blocked);

        public int TryWriteCalls => Volatile.Read(ref Observed.TryWrites);

        public int FailedTryWrites => Volatile.Read(ref Observed.FailedTryWriteCount);

        public int CompletedWriteAsyncCalls => Volatile.Read(ref Observed.CompletedWriteAsync);
    }

    private sealed class ObservedWriter(ChannelWriter<InboundMessage> inner) : ChannelWriter<InboundMessage>
    {
        internal int Blocked;
        internal int TryWrites;
        internal int FailedTryWriteCount;
        internal int CompletedWriteAsync;

        public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);

        public override bool TryWrite(InboundMessage item)
        {
            Interlocked.Increment(ref TryWrites);
            bool written = inner.TryWrite(item);
            if (!written)
            {
                Interlocked.Increment(ref FailedTryWriteCount);
            }

            return written;
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
        {
            ValueTask<bool> pending = inner.WaitToWriteAsync(cancellationToken);
            return pending.IsCompleted ? pending : AwaitBlockedAsync(pending);
        }

        // Forwards to the inner WriteAsync: the base implementation would loop over WaitToWriteAsync + TryWrite.
        public override ValueTask WriteAsync(InboundMessage item, CancellationToken cancellationToken = default)
        {
            ValueTask pending = inner.WriteAsync(item, cancellationToken);
            if (pending.IsCompleted)
            {
                Interlocked.Increment(ref CompletedWriteAsync);
                return pending;
            }

            return AwaitBlockedWriteAsync(pending);
        }

        private async ValueTask<bool> AwaitBlockedAsync(ValueTask<bool> pending)
        {
            Interlocked.Increment(ref Blocked);
            try
            {
                return await pending.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref Blocked);
            }
        }

        private async ValueTask AwaitBlockedWriteAsync(ValueTask pending)
        {
            Interlocked.Increment(ref Blocked);
            try
            {
                await pending.ConfigureAwait(false);
                Interlocked.Increment(ref CompletedWriteAsync);
            }
            finally
            {
                Interlocked.Decrement(ref Blocked);
            }
        }
    }
}
