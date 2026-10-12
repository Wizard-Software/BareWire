using System.Collections.Concurrent;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Kafka.Internal;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Transport.Kafka;

/// <summary>
/// A stopping Kafka consumer keeps its offset map and its native consumer alive, for a bounded time, for
/// messages the caller already received and still settles (for example per-key ordering lanes that finish after
/// the stop): the settlement then reaches the commit tracker before the final commit instead of hitting a
/// consumer that is already gone. The adapter-level scope in <c>SettleAsync</c> has no test seam (the adapter
/// builds its native consumer and retry producer itself), so these tests drive <see cref="KafkaConsumer"/> and
/// mirror the adapter's settlement sequence; the adapter wiring is covered by code review.
/// </summary>
public sealed class KafkaConsumerShutdownSettleTests
{
    private const string Topic = "t";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly BlockingCollection<ConsumeResult<byte[], byte[]>> _results = [];
        private readonly CancellationTokenSource _unblock = new();

        internal Rig(long[] offsets, TimeProvider? timeProvider = null, CancellationToken adapterShutdownToken = default)
        {
            Native = Substitute.For<IConsumer<byte[], byte[]>>();
            Channel = System.Threading.Channels.Channel.CreateBounded<InboundMessage>(10);
            Registry = new KafkaConsumerRegistry();
            Logger = new CapturingLogger();

            foreach (long offset in offsets)
            {
                _results.Add(new ConsumeResult<byte[], byte[]>
                {
                    Topic = Topic,
                    Partition = 0,
                    Offset = offset,
                    Message = new Message<byte[], byte[]> { Value = [1, 2, 3], Headers = new Headers() },
                }, CancellationToken.None);
            }

            Native.Consume(Arg.Any<CancellationToken>()).Returns(call => Poll(call.Arg<CancellationToken>()));

            Consumer = new KafkaConsumer(
                Native, Channel, Registry, ConsumerId, Topic, Logger,
                timeProvider: timeProvider, adapterShutdownToken: adapterShutdownToken);
            Registry.Register(ConsumerId, Consumer);
            Consumer.StartLoop();
        }

        internal const string ConsumerId = "c-1";

        internal IConsumer<byte[], byte[]> Native { get; }

        internal Channel<InboundMessage> Channel { get; }

        internal KafkaConsumerRegistry Registry { get; }

        internal CapturingLogger Logger { get; }

        internal KafkaConsumer Consumer { get; }

        // Hands out the prepared results one by one, then blocks like a poll with no data until it is cancelled.
        private ConsumeResult<byte[], byte[]>? Poll(CancellationToken ct)
        {
            if (_results.TryTake(out ConsumeResult<byte[], byte[]>? next))
            {
                return next;
            }

            ct.WaitHandle.WaitOne();
            ct.ThrowIfCancellationRequested();
            return null;
        }

        internal async Task<InboundMessage> ReadAsync() =>
            await Channel.Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Waits (condition based, not an assertion on elapsed time) until the loop has registered n offsets.
        internal async Task WaitForPendingAsync(int n)
        {
            using var timeout = new CancellationTokenSource(Guard);
            while (Registry.PendingSettlementCount(ConsumerId) < n)
            {
                await Task.Delay(1, timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Consumer.DisposeAsync();
            _unblock.Dispose();
            _results.Dispose();
        }
    }

    // Mirrors KafkaTransportAdapter.SettleAsync (Ack, retry/DLQ disabled, or republish in flight): scope, then evict
    // the offset once, then (optionally awaiting the republish) complete it in the tracker.
    private static async Task AckLikeAdapterAsync(
        Rig rig, InboundMessage message, Task? republishGate = null, TaskCompletionSource? entered = null)
    {
        using KafkaConsumerRegistry.SettlementScope settling = rig.Registry.BeginSettlement(Rig.ConsumerId);
        TopicPartitionOffset? tpo = rig.Registry.TryEvictOffset(Rig.ConsumerId, message.DeliveryTag);
        entered?.SetResult();
        if (republishGate is not null)
        {
            await republishGate;
        }

        tpo.Should().NotBeNull("the offset map must still exist while the consumer waits for the settlement");
        rig.Consumer.CommitTracker.Complete(tpo!.Partition.Value, tpo.Offset.Value);
    }

    private static async Task WaitUntilStoppedOrWaitingAsync(Task stop, Rig rig) =>
        await Task.WhenAny(stop, rig.Logger.WaitStarted.Task).WaitAsync(Guard, TestContext.Current.CancellationToken);

    [Fact]
    public async Task StopAsync_InFlightMessageAckedDuringWait_OffsetStoredBeforeClose()
    {
        // Arrange
        await using var rig = new Rig([7]);
        InboundMessage m7 = await rig.ReadAsync();

        // Act — stop while m7 is registered but not settled; settle it only once the consumer waits.
        Task stop = rig.Consumer.StopAsync();
        await WaitUntilStoppedOrWaitingAsync(stop, rig);
        await AckLikeAdapterAsync(rig, m7);
        await stop.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        Received.InOrder(() =>
        {
            rig.Native.StoreOffset(Arg.Is<TopicPartitionOffset>(t => t.Offset.Value == 8));
            rig.Native.Close();
        });
    }

    [Fact]
    public async Task StopAsync_SettlementInProgress_ClosesOnlyAfterItEnds()
    {
        // Arrange — a settlement already evicted its offset and is still republishing (in-flight 0, in-progress 1).
        await using var rig = new Rig([7]);
        InboundMessage m7 = await rig.ReadAsync();
        var republishGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task settle = Task.Run(() => AckLikeAdapterAsync(rig, m7, republishGate.Task, settleEntered), TestContext.Current.CancellationToken);
        await settleEntered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Act
        Task stop = rig.Consumer.StopAsync();
        await WaitUntilStoppedOrWaitingAsync(stop, rig);

        // Assert — the native consumer is still open while the settlement runs
        rig.Native.DidNotReceive().Close();

        republishGate.SetResult();
        await settle.WaitAsync(Guard, TestContext.Current.CancellationToken);
        await stop.WaitAsync(Guard, TestContext.Current.CancellationToken);
        rig.Native.Received(1).Close();
        rig.Native.Received(1).StoreOffset(Arg.Is<TopicPartitionOffset>(t => t.Offset.Value == 8));
    }

    [Fact]
    public async Task StopAsync_InFlightNeverAcked_ClosesAfterBudget()
    {
        // Arrange
        var fake = new FakeTimeProvider();
        await using var rig = new Rig([7], fake);
        _ = await rig.ReadAsync();

        // Act
        Task stop = rig.Consumer.StopAsync();
        await WaitUntilStoppedOrWaitingAsync(stop, rig);
        await FakeTimePump.AdvanceUntilCompletedAsync(fake, stop, KafkaSettlementDrain.Budget, Guard);

        // Assert
        rig.Logger.Entries.Count(e => e.EventName == "LogSettlementBudgetElapsed" && e.Level == LogLevel.Warning)
            .Should().Be(1);
        rig.Native.Received(1).Close();
        rig.Native.DidNotReceive().StoreOffset(Arg.Any<TopicPartitionOffset>());
    }

    [Fact]
    public async Task StopAsync_NoInFlight_ClosesWithoutWaiting()
    {
        // Arrange — fake time that is never advanced: any wait would hang.
        var fake = new FakeTimeProvider();
        await using var rig = new Rig([7], fake);
        InboundMessage m7 = await rig.ReadAsync();
        await AckLikeAdapterAsync(rig, m7);

        // Act
        await rig.Consumer.StopAsync().WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        rig.Logger.Entries.Should().NotContain(e => e.EventName == "LogAwaitingSettlements");
        rig.Native.Received(1).Close();
    }

    [Fact]
    public async Task StopAsync_BufferedNotYieldedMessages_EvictedAndDoNotDelayStop()
    {
        // Arrange — three offsets polled; only m7 is read and acked, m8 and m9 stay buffered in the channel.
        var fake = new FakeTimeProvider();
        await using var rig = new Rig([7, 8, 9], fake);
        await rig.WaitForPendingAsync(3);
        InboundMessage m7 = await rig.ReadAsync();
        await AckLikeAdapterAsync(rig, m7);

        // Act — fake time is never advanced, so waiting for the buffered offsets would hang.
        await rig.Consumer.StopAsync().WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        rig.Registry.PendingSettlementCount(Rig.ConsumerId).Should().Be(0);
        rig.Logger.Entries.Should().NotContain(e => e.EventName == "LogAwaitingSettlements");
        rig.Native.Received(1).StoreOffset(Arg.Any<TopicPartitionOffset>());
        rig.Native.Received(1).Close();
    }

    [Fact]
    public async Task StopAsync_ConcurrentCallers_ShareOneStop()
    {
        // Arrange
        await using var rig = new Rig([]);

        // Act
        Task a = rig.Consumer.StopAsync();
        Task b = rig.Consumer.StopAsync();

        // Assert
        ReferenceEquals(a, b).Should().BeTrue();
        await a.WaitAsync(Guard, TestContext.Current.CancellationToken);
        rig.Native.Received(1).Close();
    }

    [Fact]
    public async Task StopAsync_AdapterShutdownTokenCancelled_SkipsWait()
    {
        // Arrange — the adapter is being disposed; fake time is never advanced, so any wait would hang.
        var fake = new FakeTimeProvider();
        await using var rig = new Rig([7], fake, new CancellationToken(canceled: true));
        _ = await rig.ReadAsync();

        // Act
        await rig.Consumer.StopAsync().WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Assert
        rig.Logger.Entries.Should().NotContain(e => e.EventName == "LogAwaitingSettlements");
        rig.Native.Received(1).Close();
    }

    [Fact]
    public async Task DisposeAsync_AfterStop_DisposesNativeConsumerOnce()
    {
        // Arrange
        var rig = new Rig([]);
        await rig.Consumer.StopAsync().WaitAsync(Guard, TestContext.Current.CancellationToken);

        // Act
        await rig.Consumer.DisposeAsync();

        // Assert
        rig.Native.Received(1).Close();
        rig.Native.Received(1).Dispose();
        await rig.DisposeAsync();
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<LogEntry> _entries = [];

        /// <summary>Completes when the consumer logs that it waits for pending settlements.</summary>
        internal TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(eventId.Name, logLevel));
            }

            if (eventId.Name == "LogAwaitingSettlements")
            {
                WaitStarted.TrySetResult();
            }
        }
    }

    private sealed record LogEntry(string? EventName, LogLevel Level);
}
