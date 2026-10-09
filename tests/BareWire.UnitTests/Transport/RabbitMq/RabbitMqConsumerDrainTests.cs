#pragma warning disable CA2012 // NSubstitute .Returns()/Received on ValueTask is a known false positive
using System.Buffers;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Internal;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Unit tests for the shutdown members of <see cref="RabbitMqConsumer"/>: the single-flight
/// <c>basic.cancel</c>, the token-checked read and the drain of deliveries that were never handed to a caller.
/// </summary>
public sealed class RabbitMqConsumerDrainTests
{
    private const string Tag = "ctag";

    private static IChannel CreateChannel(bool open = true)
    {
        IChannel channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(open);
        channel.BasicCancelAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        channel.BasicNackAsync(Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ValueTask.CompletedTask);
        return channel;
    }

    private static RabbitMqConsumer CreateConsumer(IChannel channel, Channel<InboundMessage> inbound) =>
        new(channel, inbound, new RabbitMqHeaderMapper(), "c1") { AssignedTag = Tag };

    /// <summary>Buffers a message backed by a rented buffer; disposal is observable through PooledBuffer.</summary>
    private static InboundMessage Buffer(Channel<InboundMessage> inbound, ulong tag)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8);
        var message = new InboundMessage(
            $"m-{tag}", new Dictionary<string, string>(), new ReadOnlySequence<byte>(buffer, 0, 3), tag, buffer);
        inbound.Writer.TryWrite(message).Should().BeTrue();
        return message;
    }

    // ── EnsureCancelledAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task EnsureCancelledAsync_CalledConcurrently_SendsBasicCancelOnce()
    {
        // Arrange
        IChannel channel = CreateChannel();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.BasicCancelAsync(Tag, false, Arg.Any<CancellationToken>()).Returns(gate.Task);
        RabbitMqConsumer consumer = CreateConsumer(channel, Channel.CreateBounded<InboundMessage>(4));

        // Act
        Task<bool> first = consumer.EnsureCancelledAsync();
        Task<bool> second = consumer.EnsureCancelledAsync();
        gate.SetResult();
        bool[] results = await Task.WhenAll(first, second);

        // Assert
        results.Should().OnlyContain(r => r);
        await channel.Received(1).BasicCancelAsync(Tag, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureCancelledAsync_FirstCallFails_SecondCallDoesNotSendAgain()
    {
        // Arrange
        IChannel channel = CreateChannel();
        channel.BasicCancelAsync(Tag, false, Arg.Any<CancellationToken>()).Throws(
            new InvalidOperationException("boom"));
        RabbitMqConsumer consumer = CreateConsumer(channel, Channel.CreateBounded<InboundMessage>(4));

        // Act
        bool first = await consumer.EnsureCancelledAsync();
        bool second = await consumer.EnsureCancelledAsync();

        // Assert — the failure is remembered, never retried, and never thrown.
        first.Should().BeFalse();
        second.Should().BeFalse();
        consumer.CancelFailure.Should().BeOfType<InvalidOperationException>();
        await channel.Received(1).BasicCancelAsync(Tag, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureCancelledAsync_ChannelNotOpen_SkipsCancelAndReturnsFalse()
    {
        IChannel channel = CreateChannel(open: false);
        RabbitMqConsumer consumer = CreateConsumer(channel, Channel.CreateBounded<InboundMessage>(4));

        bool result = await consumer.EnsureCancelledAsync();

        result.Should().BeFalse();
        await channel.DidNotReceive().BasicCancelAsync(
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    // ── ReadUntilCancelledAsync ───────────────────────────────────────────────

    [Fact]
    public async Task ReadUntilCancelledAsync_TokenCancelledWithMessagesBuffered_StopsWithoutReadingNext()
    {
        // Arrange
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(10);
        RabbitMqConsumer consumer = CreateConsumer(CreateChannel(), inbound);
        Buffer(inbound, 1);
        Buffer(inbound, 2);
        Buffer(inbound, 3);
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = consumer.ReadUntilCancelledAsync(cts.Token).GetAsyncEnumerator();

        // Act
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        await cts.CancelAsync();
        Func<Task> next = async () => await enumerator.MoveNextAsync();

        // Assert
        await next.Should().ThrowAsync<OperationCanceledException>();
        inbound.Reader.Count.Should().Be(2, "the remaining deliveries must stay in the buffer for the drain");
        await enumerator.DisposeAsync();
    }

    // ── DrainAndRequeueAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task DrainAndRequeueAsync_WithBufferedDeliveries_NacksEachWithRequeueAndDisposes()
    {
        // Arrange
        IChannel channel = CreateChannel();
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(10);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        InboundMessage m2 = Buffer(inbound, 2);
        InboundMessage m3 = Buffer(inbound, 3);
        consumer.CompleteWriter();

        // Act
        RabbitMqConsumer.DrainResult result = await consumer.DrainAndRequeueAsync(CancellationToken.None);

        // Assert
        result.Should().Be(new RabbitMqConsumer.DrainResult(2, 2, 0, null, 0, null));
        await channel.Received(1).BasicNackAsync(2UL, false, true, Arg.Any<CancellationToken>());
        await channel.Received(1).BasicNackAsync(3UL, false, true, Arg.Any<CancellationToken>());
        m2.PooledBuffer.Should().BeNull("the pooled buffer must be returned");
        m3.PooledBuffer.Should().BeNull("the pooled buffer must be returned");
    }

    [Fact]
    public async Task DrainAndRequeueAsync_NackThrows_DisposesAllAndSwitchesToDisposeOnly()
    {
        // Arrange — the first nack fails; the remaining messages are only disposed.
        IChannel channel = CreateChannel();
        channel.BasicNackAsync(1UL, Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("nack failed"));
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(10);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        InboundMessage[] messages = [Buffer(inbound, 1), Buffer(inbound, 2), Buffer(inbound, 3)];
        consumer.CompleteWriter();

        // Act
        Func<Task<RabbitMqConsumer.DrainResult>> drain = () => consumer.DrainAndRequeueAsync(CancellationToken.None);
        RabbitMqConsumer.DrainResult result = await drain();

        // Assert
        result.Drained.Should().Be(3);
        result.Requeued.Should().Be(0);
        result.SkippedNacks.Should().Be(2);
        result.FirstFailure.Should().BeOfType<InvalidOperationException>();
        result.FirstFailedTag.Should().Be(1UL);
        result.FirstFailedMessageId.Should().Be("m-1");
        messages.Should().OnlyContain(m => m.PooledBuffer == null, "every buffered message must be disposed");
        await channel.Received(1).BasicNackAsync(
            Arg.Any<ulong>(),
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DrainAndRequeueAsync_ChannelClosed_DisposesWithoutNack()
    {
        // Arrange
        IChannel channel = CreateChannel(open: false);
        Channel<InboundMessage> inbound = Channel.CreateBounded<InboundMessage>(10);
        RabbitMqConsumer consumer = CreateConsumer(channel, inbound);
        InboundMessage[] messages = [Buffer(inbound, 2), Buffer(inbound, 3)];
        consumer.CompleteWriter();

        // Act
        RabbitMqConsumer.DrainResult result = await consumer.DrainAndRequeueAsync(CancellationToken.None);

        // Assert
        result.Drained.Should().Be(2);
        result.FirstFailure.Should().BeNull(
            "a closed channel is not a nack failure; the broker returns the deliveries itself");
        messages.Should().OnlyContain(m => m.PooledBuffer == null);
        await channel.DidNotReceive().BasicNackAsync(
            Arg.Any<ulong>(),
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }
}
