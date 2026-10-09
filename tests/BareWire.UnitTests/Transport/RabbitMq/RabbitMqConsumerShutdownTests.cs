#pragma warning disable CA2012 // NSubstitute .Returns()/Received on ValueTask is a known false positive
using System.Reflection;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Guards the consumer shutdown order of <see cref="RabbitMqTransportAdapter.ConsumeAsync"/>: the AMQP
/// consumer is cancelled first, then every delivery still sitting in the adapter's buffer is handed back to
/// the broker (nack, requeue) instead of being left unacknowledged, and a cancelled token never reads a
/// further buffered message. The adapter is driven through its public <c>ConsumeAsync</c> entry point with a
/// substituted <see cref="IConnection"/> / <see cref="IChannel"/> injected via reflection (as
/// <see cref="RabbitMqTransportAdapterChannelCleanupTests"/> does for <c>_activeConsumerChannels</c>), so the
/// tests run without a broker.
/// </summary>
public sealed class RabbitMqConsumerShutdownTests
{
    private const string ConsumerTag = "ctag-1";
    private const string Queue = "shutdown-queue";

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        internal required RabbitMqTransportAdapter Adapter { get; init; }

        internal required IChannel Channel { get; init; }

        /// <summary>The consumer the adapter passed to <c>BasicConsumeAsync</c>; set once consuming starts.</summary>
        internal IAsyncBasicConsumer? Consumer { get; set; }
    }

    private static Harness CreateHarness()
    {
        IChannel channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(true);
        channel.BasicQosAsync(Arg.Any<uint>(), Arg.Any<ushort>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        channel.BasicCancelAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        channel.BasicNackAsync(Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ValueTask.CompletedTask);
        channel.CloseAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        channel.DisposeAsync().Returns(callInfo => ValueTask.CompletedTask);

        IConnection connection = Substitute.For<IConnection>();
        connection.IsOpen.Returns(true);
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));

        var adapter = new RabbitMqTransportAdapter(
            new RabbitMqTransportOptions { ConnectionString = "amqp://guest:guest@localhost:5672/" },
            NullLogger<RabbitMqTransportAdapter>.Instance);

        // EnsureConnectedAsync returns early when _connection is open, so no broker connection is attempted.
        typeof(RabbitMqTransportAdapter)
            .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(adapter, connection);

        var harness = new Harness { Adapter = adapter, Channel = channel };

        channel.BasicConsumeAsync(
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<IAsyncBasicConsumer>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                harness.Consumer = callInfo.ArgAt<IAsyncBasicConsumer>(6);
                return Task.FromResult(ConsumerTag);
            });

        return harness;
    }

    private static Task DeliverAsync(Harness harness, ulong deliveryTag) =>
        harness.Consumer!.HandleBasicDeliverAsync(
            ConsumerTag,
            deliveryTag,
            redelivered: false,
            exchange: string.Empty,
            routingKey: Queue,
            properties: new BasicProperties { MessageId = $"m-{deliveryTag}" },
            body: new byte[] { 1, 2, 3 },
            cancellationToken: CancellationToken.None);

    /// <summary>
    /// Starts consuming, delivers tags 1..3, and reads the first message (tag 1) so the enumerator is suspended
    /// at a <c>yield return</c> with deliveries 2 and 3 buffered — the state a runner is in when it is cancelled.
    /// </summary>
    private static async Task<IAsyncEnumerator<InboundMessage>> StartWithThreeDeliveriesAsync(
        Harness harness, CancellationToken token)
    {
        IAsyncEnumerator<InboundMessage> enumerator = harness.Adapter
            .ConsumeAsync(Queue, new FlowControlOptions { MaxInFlightMessages = 10, InternalQueueCapacity = 10 }, token)
            .GetAsyncEnumerator(token);

        ValueTask<bool> firstMove = enumerator.MoveNextAsync();
        while (harness.Consumer is null)
        {
            await Task.Yield();
        }

        await DeliverAsync(harness, 1);
        await DeliverAsync(harness, 2);
        await DeliverAsync(harness, 3);

        (await firstMove).Should().BeTrue();
        enumerator.Current.DeliveryTag.Should().Be(1UL);
        return enumerator;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_TokenCancelledWithMessagesBuffered_ThrowsOperationCanceledInsteadOfYieldingNext()
    {
        // Arrange
        Harness harness = CreateHarness();
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = await StartWithThreeDeliveriesAsync(harness, cts.Token);

        // Act
        await cts.CancelAsync();
        Func<Task> moveNext = async () => await enumerator.MoveNextAsync();

        // Assert — the buffered delivery 2 must not be handed to the caller once the token is cancelled.
        await moveNext.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled consumer must not read another message out of the buffer");

        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task ConsumeAsync_Cancelled_CancelsConsumerThenRequeuesBufferedDeliveries()
    {
        // Arrange
        Harness harness = CreateHarness();
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = await StartWithThreeDeliveriesAsync(harness, cts.Token);

        // Act — cancel, let the iterator observe it, then release the enumerator as the runner does.
        await cts.CancelAsync();
        try
        {
            await enumerator.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected once the token is honoured before every read.
        }

        await enumerator.DisposeAsync();

        // Assert — stop the broker first, then give back what was never handed to the caller.
        Received.InOrder(() =>
        {
            harness.Channel.BasicCancelAsync(ConsumerTag, false, Arg.Any<CancellationToken>());
            harness.Channel.BasicNackAsync(2UL, false, true, Arg.Any<CancellationToken>());
            harness.Channel.BasicNackAsync(3UL, false, true, Arg.Any<CancellationToken>());
        });

        // The channel stays open: delivery 1 was handed out and is settled by the caller via SettleAsync.
        await harness.Channel.DidNotReceive().CloseAsync(Arg.Any<CancellationToken>());
        await harness.Channel.DidNotReceive().BasicNackAsync(
            1UL,
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_RequeueOfBufferedDeliveryThrows_ShutdownCompletesWithoutThrowing()
    {
        // Arrange
        Harness harness = CreateHarness();
        harness.Channel
            .BasicNackAsync(2UL, Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("nack failed"));
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = await StartWithThreeDeliveriesAsync(harness, cts.Token);

        // Act
        await cts.CancelAsync();
        try
        {
            await enumerator.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
        }

        Func<Task> dispose = async () => await enumerator.DisposeAsync();

        // Assert — the failed nack is handled inside the adapter and never escapes from the finally block,
        // and the requeue of the buffered delivery was actually attempted.
        await dispose.Should().NotThrowAsync();
        await harness.Channel.Received().BasicNackAsync(2UL, false, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumeAsync_ChannelClosedAtShutdown_DoesNotCancelOrNackAndDoesNotThrow()
    {
        // Arrange
        Harness harness = CreateHarness();
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = await StartWithThreeDeliveriesAsync(harness, cts.Token);

        // Act — the broker connection dropped: the channel is gone, the broker returns unacked deliveries itself.
        harness.Channel.IsOpen.Returns(false);
        await cts.CancelAsync();
        try
        {
            await enumerator.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
        }

        Func<Task> dispose = async () => await enumerator.DisposeAsync();

        // Assert
        await dispose.Should().NotThrowAsync();
        await harness.Channel.DidNotReceive().BasicCancelAsync(
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
        await harness.Channel.DidNotReceive().BasicNackAsync(
            Arg.Any<ulong>(),
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleBasicDeliverAsync_ManyDeliveriesAfterWriterCompleted_NacksWithRequeueAtMostOnce()
    {
        // Arrange — a consumer whose writer is already completed models the window between the end of the
        // read loop and a basic.cancel that was skipped (channel not open) or failed.
        IChannel channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(true);
        channel.BasicNackAsync(Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ValueTask.CompletedTask);
        channel.BasicCancelAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Channel<InboundMessage> inbound = System.Threading.Channels.Channel.CreateBounded<InboundMessage>(10);
        var consumer = new RabbitMqConsumer(channel, inbound, new RabbitMqHeaderMapper(), "c1");
        inbound.Writer.TryComplete();

        // Act — the broker keeps pushing the same returned messages to the stopped consumer.
        for (ulong tag = 1; tag <= 20; tag++)
        {
            await consumer.HandleBasicDeliverAsync(
                ConsumerTag, tag, redelivered: true, string.Empty, Queue,
                new BasicProperties { MessageId = $"m-{tag}" }, new byte[] { 1 }, CancellationToken.None);
        }

        // Assert — an unbounded nack-requeue loop is the shutdown spin this test guards against; the number of
        // requeueing nacks after the writer ended must stay bounded.
        int requeueNacks = channel.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IChannel.BasicNackAsync)
                && call.GetArguments()[2] is true);
        requeueNacks.Should().BeLessThanOrEqualTo(1,
            "a consumer that has stopped reading must not nack-requeue every redelivery in a loop");
    }

    // ── SettleAsync(Requeue) while shutting down ──────────────────────────────

    [Fact]
    public async Task SettleAsync_RequeueWhileStopRequested_CancelsConsumerBeforeNack()
    {
        // Arrange
        Harness harness = CreateHarness();
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = await StartWithThreeDeliveriesAsync(harness, cts.Token);
        InboundMessage inFlight = enumerator.Current;
        await cts.CancelAsync();

        // Act — the runner requeues the in-flight message while the generator is still suspended.
        await harness.Adapter.SettleAsync(SettlementAction.Requeue, inFlight, CancellationToken.None);

        // Assert
        Received.InOrder(() =>
        {
            harness.Channel.BasicCancelAsync(ConsumerTag, false, Arg.Any<CancellationToken>());
            harness.Channel.BasicNackAsync(1UL, false, true, Arg.Any<CancellationToken>());
        });

        await enumerator.DisposeAsync();

        // Single-flight: the generator's finally reuses the cancel that settlement already sent.
        await harness.Channel.Received(1).BasicCancelAsync(
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SettleAsync_RequeueWhileNotStopping_DoesNotCancelConsumer()
    {
        // Arrange
        Harness harness = CreateHarness();
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = await StartWithThreeDeliveriesAsync(harness, cts.Token);

        // Act
        await harness.Adapter.SettleAsync(SettlementAction.Requeue, enumerator.Current, CancellationToken.None);

        // Assert
        await harness.Channel.DidNotReceive().BasicCancelAsync(
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
        await harness.Channel.Received(1).BasicNackAsync(1UL, false, true, Arg.Any<CancellationToken>());

        await cts.CancelAsync();
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task SettleAsync_RequeueWhileStopRequestedAndCancelFails_StillSendsNack()
    {
        // Arrange
        Harness harness = CreateHarness();
        harness.Channel.BasicCancelAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("cancel failed"));
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = await StartWithThreeDeliveriesAsync(harness, cts.Token);
        InboundMessage inFlight = enumerator.Current;
        await cts.CancelAsync();

        // Act
        Func<Task> settle = () => harness.Adapter.SettleAsync(
            SettlementAction.Requeue,
            inFlight,
            CancellationToken.None);

        // Assert
        await settle.Should().NotThrowAsync();
        await harness.Channel.Received(1).BasicNackAsync(1UL, false, true, Arg.Any<CancellationToken>());

        await enumerator.DisposeAsync();
        await harness.Channel.Received(1).BasicCancelAsync(
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    // ── Cancel failed or skipped: the consumer channel is closed after the drain ──

    [Fact]
    public async Task ConsumeAsync_CancelFails_ClosesConsumerChannelAfterDraining()
    {
        // Arrange
        Harness harness = CreateHarness();
        harness.Channel.BasicCancelAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("cancel failed"));
        using var cts = new CancellationTokenSource();
        IAsyncEnumerator<InboundMessage> enumerator = await StartWithThreeDeliveriesAsync(harness, cts.Token);

        // Act
        await cts.CancelAsync();
        try
        {
            await enumerator.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
        }

        await enumerator.DisposeAsync();

        // Assert — the broker may keep feeding a consumer that could not be cancelled, so the channel is closed
        // (after the buffered deliveries were requeued) and the broker returns every unsettled delivery once.
        Received.InOrder(() =>
        {
            harness.Channel.BasicNackAsync(2UL, false, true, Arg.Any<CancellationToken>());
            harness.Channel.BasicNackAsync(3UL, false, true, Arg.Any<CancellationToken>());
            harness.Channel.CloseAsync(Arg.Any<CancellationToken>());
        });
    }
}
