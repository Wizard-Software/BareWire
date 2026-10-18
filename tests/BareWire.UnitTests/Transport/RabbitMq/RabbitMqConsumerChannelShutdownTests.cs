#pragma warning disable CA2012 // NSubstitute .Returns()/Received on ValueTask is a known false positive
using System.Buffers;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Internal;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Unit tests for how <see cref="RabbitMqConsumer"/> reacts to a channel shutdown event: with automatic recovery
/// the buffered deliveries of the dead channel are dropped (never settled, never handed to the runner) while the
/// writer stays open for the recovered consumer; otherwise the writer is completed.
/// </summary>
public sealed class RabbitMqConsumerChannelShutdownTests
{
    private const string Tag = "ctag";

    // Bounds every read that could otherwise wait forever when the code under test is wrong.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private static IChannel CreateChannel()
    {
        IChannel channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(true);
        channel.BasicNackAsync(Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ValueTask.CompletedTask);
        return channel;
    }

    private static RabbitMqConsumer CreateConsumer(
        IChannel channel,
        Channel<InboundMessage> inbound,
        bool recovery = true,
        Func<bool>? connectionProbe = null) =>
        new(channel, inbound, new RabbitMqHeaderMapper(), "c1")
        {
            AssignedTag = Tag,
            AutomaticRecoveryEnabled = recovery,
            IsConnectionOpen = connectionProbe ?? (() => false),
        };

    private static ShutdownEventArgs Shutdown(
        ShutdownInitiator initiator = ShutdownInitiator.Peer,
        ushort replyCode = 320) =>
        new(initiator, replyCode, "CONNECTION_FORCED");

    /// <summary>Delivers through the real handler so the consumer tracks the highest delivery tag.</summary>
    private static Task DeliverAsync(RabbitMqConsumer consumer, ulong tag) =>
        consumer.HandleBasicDeliverAsync(
            Tag,
            tag,
            redelivered: false,
            exchange: string.Empty,
            routingKey: "rk",
            Substitute.For<IReadOnlyBasicProperties>(),
            new byte[] { 1, 2, 3 });

    /// <summary>
    /// Returns the messages currently buffered (so their pooled buffers can be observed after the code under
    /// test disposes them) and puts them back in the same order.
    /// </summary>
    private static List<InboundMessage> CaptureBuffered(Channel<InboundMessage> inbound)
    {
        var captured = new List<InboundMessage>();
        while (inbound.Reader.TryRead(out InboundMessage? message))
        {
            captured.Add(message);
        }

        foreach (InboundMessage message in captured)
        {
            inbound.Writer.TryWrite(message).Should().BeTrue();
        }

        return captured;
    }

    /// <summary>Writes a message straight into the buffer, bypassing the tag tracking of the handler.</summary>
    private static InboundMessage Inject(Channel<InboundMessage> inbound, ulong tag)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8);
        var message = new InboundMessage(
            $"m-{tag}", new Dictionary<string, string>(), new ReadOnlySequence<byte>(buffer, 0, 3), tag, buffer);
        inbound.Writer.TryWrite(message).Should().BeTrue();
        return message;
    }

    private static void AssertNothingSettled(IChannel channel) =>
        channel.ReceivedCalls().Should().NotContain(
            call => call.GetMethodInfo().Name == "BasicAckAsync"
                || call.GetMethodInfo().Name == "BasicNackAsync"
                || call.GetMethodInfo().Name == "BasicRejectAsync");

    private static async Task DeliverRangeAsync(RabbitMqConsumer consumer, ulong first, ulong last)
    {
        for (ulong tag = first; tag <= last; tag++)
        {
            await DeliverAsync(consumer, tag);
        }
    }

    // ── Recovery expected ─────────────────────────────────────────────────────

    [Fact]
    public async Task HandleChannelShutdownAsync_RecoveryExpected_DiscardsBufferedDeliveriesWithoutSettling()
    {
        // Arrange
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        await DeliverRangeAsync(consumer, 1, 3);
        List<InboundMessage> buffered = CaptureBuffered(inbound);

        // Act
        await consumer.HandleChannelShutdownAsync(channel, Shutdown());

        // Assert
        inbound.Reader.TryRead(out _).Should().BeFalse();
        consumer.DiscardedStaleDeliveries.Should().Be(3);
        buffered.Should().HaveCount(3).And.OnlyContain(m => m.PooledBuffer == null, "pooled buffers must be returned");
        AssertNothingSettled(channel);
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_RecoveryExpected_KeepsWriterOpenForRecoveredDeliveries()
    {
        // Arrange
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        await DeliverRangeAsync(consumer, 1, 3);
        await consumer.HandleChannelShutdownAsync(channel, Shutdown());

        // Act
        await DeliverAsync(consumer, 4);
        using var cts = new CancellationTokenSource(ReadTimeout);
        await using IAsyncEnumerator<InboundMessage> reader =
            consumer.ReadUntilCancelledAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        bool hasNext = await reader.MoveNextAsync();

        // Assert
        inbound.Reader.Completion.IsCompleted.Should().BeFalse();
        hasNext.Should().BeTrue();
        reader.Current.DeliveryTag.Should().Be(4UL);
        AssertNothingSettled(channel);
    }

    [Fact]
    public async Task ReadUntilCancelledAsync_StaleDeliveryReachesReader_DisposesItWithoutYielding()
    {
        // Arrange
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        await DeliverRangeAsync(consumer, 1, 2);
        await consumer.HandleChannelShutdownAsync(channel, Shutdown());

        // A stale entry that slipped in after the purge (race), followed by a delivery of the recovered channel.
        InboundMessage stale = Inject(inbound, 2);
        await DeliverAsync(consumer, 5);

        // Act
        using var cts = new CancellationTokenSource(ReadTimeout);
        await using IAsyncEnumerator<InboundMessage> reader =
            consumer.ReadUntilCancelledAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        bool hasNext = await reader.MoveNextAsync();

        // Assert
        hasNext.Should().BeTrue();
        reader.Current.DeliveryTag.Should().Be(5UL, "the stale tag 2 must never reach the caller");
        stale.PooledBuffer.Should().BeNull("the stale message must be disposed");
        consumer.DiscardedStaleDeliveries.Should().Be(3, "two purged at shutdown plus the stale entry");
        AssertNothingSettled(channel);
    }

    [Fact]
    public async Task HandleBasicDeliverAsync_RecoveredWindowAfterFullStaleBuffer_BuffersWithoutNack()
    {
        // Arrange — the buffer is full of deliveries of the dead channel.
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(2);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        await DeliverRangeAsync(consumer, 1, 2);
        await consumer.HandleChannelShutdownAsync(channel, Shutdown());

        // Act
        await DeliverRangeAsync(consumer, 3, 4);

        // Assert
        var tags = new List<ulong>();
        while (inbound.Reader.TryRead(out InboundMessage? message))
        {
            tags.Add(message.DeliveryTag);
        }

        tags.Should().Equal(3UL, 4UL);
        await channel.DidNotReceive().BasicNackAsync(
            Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_LibraryInitiated_TreatedAsRecoveryExpected()
    {
        // Arrange
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        await DeliverAsync(consumer, 1);

        // Act
        await consumer.HandleChannelShutdownAsync(channel, Shutdown(ShutdownInitiator.Library, 541));

        // Assert
        inbound.Reader.Completion.IsCompleted.Should().BeFalse();
        consumer.DiscardedStaleDeliveries.Should().Be(1);
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_CalledTwice_WatermarkNeverLowers()
    {
        // Arrange
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        await DeliverRangeAsync(consumer, 1, 5);
        await consumer.HandleChannelShutdownAsync(channel, Shutdown());
        await DeliverAsync(consumer, 6);

        // Act
        await consumer.HandleChannelShutdownAsync(channel, Shutdown());

        // Assert
        consumer.IsStale(5).Should().BeTrue();
        consumer.IsStale(6).Should().BeTrue();
        consumer.IsStale(7).Should().BeFalse();
        consumer.DiscardedStaleDeliveries.Should().Be(6);
    }

    // ── Terminal shutdown ─────────────────────────────────────────────────────

    [Fact]
    public async Task HandleChannelShutdownAsync_ApplicationInitiated_CompletesWriterAndDiscardsBuffered()
    {
        // Arrange
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        await DeliverRangeAsync(consumer, 1, 3);
        List<InboundMessage> buffered = CaptureBuffered(inbound);

        // Act
        await consumer.HandleChannelShutdownAsync(channel, Shutdown(ShutdownInitiator.Application, 200));

        // Assert
        inbound.Reader.Completion.IsCompleted.Should().BeTrue();
        inbound.Reader.TryRead(out _).Should().BeFalse();
        consumer.DiscardedStaleDeliveries.Should().Be(3);
        buffered.Should().OnlyContain(m => m.PooledBuffer == null);
        AssertNothingSettled(channel);
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_RecoveryDisabled_CompletesWriter()
    {
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound, recovery: false);
        await DeliverAsync(consumer, 1);

        await consumer.HandleChannelShutdownAsync(channel, Shutdown());

        inbound.Reader.Completion.IsCompleted.Should().BeTrue();
        consumer.DiscardedStaleDeliveries.Should().Be(1);
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_ConnectionStillOpen_TreatedAsTerminal()
    {
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound, connectionProbe: () => true);
        await DeliverAsync(consumer, 1);

        await consumer.HandleChannelShutdownAsync(channel, Shutdown());

        inbound.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_StopRequested_TreatedAsTerminal()
    {
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        await DeliverAsync(consumer, 1);
        consumer.RequestStop();

        await consumer.HandleChannelShutdownAsync(channel, Shutdown());

        inbound.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_NoConnectionProbe_TreatedAsTerminal()
    {
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        var consumer = new RabbitMqConsumer(channel, inbound, new RabbitMqHeaderMapper(), "c1")
        {
            AssignedTag = Tag,
            AutomaticRecoveryEnabled = true,
            IsConnectionOpen = null,
        };
        await DeliverAsync(consumer, 1);

        await consumer.HandleChannelShutdownAsync(channel, Shutdown());

        inbound.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_ProbeThrowsObjectDisposed_TreatedAsTerminalWithoutThrowing()
    {
        // Arrange — the probe reads a connection that was disposed in the meantime.
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(
            channel, inbound, connectionProbe: () => throw new ObjectDisposedException("connection"));
        await DeliverAsync(consumer, 1);

        // Act
        Func<Task> act = () => consumer.HandleChannelShutdownAsync(channel, Shutdown());

        // Assert
        await act.Should().NotThrowAsync();
        inbound.Reader.Completion.IsCompleted.Should().BeTrue();
        consumer.DiscardedStaleDeliveries.Should().Be(1);
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_PeerAccessRefused_TreatedAsTerminal()
    {
        // Arrange — RabbitMQ.Client 7.2.1 does not recover a connection closed by the peer with 403.
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        await DeliverAsync(consumer, 1);

        // Act
        await consumer.HandleChannelShutdownAsync(channel, Shutdown(ShutdownInitiator.Peer, 403));

        // Assert
        inbound.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_TerminalWithNonStaleBuffered_DisposesWithoutSettling()
    {
        // Arrange — a buffered entry above the watermark cannot be re-buffered once the writer is completed.
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound, recovery: false);
        await DeliverAsync(consumer, 1);
        InboundMessage fresh = Inject(inbound, 99);

        // Act
        await consumer.HandleChannelShutdownAsync(channel, Shutdown());

        // Assert
        fresh.PooledBuffer.Should().BeNull("a message that cannot be re-buffered must return its pooled buffer");
        inbound.Reader.TryRead(out _).Should().BeFalse();
        AssertNothingSettled(channel);
    }

    // ── Drain ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DrainAndRequeueAsync_StaleTagsOnReopenedChannel_DisposesWithoutNack()
    {
        // Arrange — the channel was recovered (IsOpen again) and the buffer still holds entries of the dead one.
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(8);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        await DeliverRangeAsync(consumer, 1, 2);
        await consumer.HandleChannelShutdownAsync(channel, Shutdown());
        InboundMessage stale1 = Inject(inbound, 1);
        InboundMessage stale2 = Inject(inbound, 2);
        await DeliverAsync(consumer, 3);

        // Act
        RabbitMqConsumer.DrainResult result = await consumer.DrainAndRequeueAsync(CancellationToken.None);

        // Assert
        result.Drained.Should().Be(3);
        result.Requeued.Should().Be(1);
        result.SkippedNacks.Should().Be(0);
        result.DiscardedStale.Should().Be(2);
        result.FirstFailure.Should().BeNull();
        stale1.PooledBuffer.Should().BeNull();
        stale2.PooledBuffer.Should().BeNull();
        await channel.Received(1).BasicNackAsync(
            Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await channel.Received(1).BasicNackAsync(3UL, false, true, Arg.Any<CancellationToken>());
    }
}
