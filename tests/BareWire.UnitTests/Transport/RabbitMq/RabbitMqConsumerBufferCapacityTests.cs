#pragma warning disable CA2012 // NSubstitute .Returns()/Received on ValueTask is a known false positive
using System.Reflection;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Guards the sizing of the adapter's internal buffer in <see cref="RabbitMqTransportAdapter.ConsumeAsync"/>:
/// the buffer must hold at least a full prefetch window (the broker never has more unacknowledged deliveries
/// for the consumer than the prefetch count), so a slow reader never causes nack-requeues during normal
/// operation, and invalid <see cref="FlowControlOptions"/> are rejected before a channel is opened.
/// </summary>
public sealed class RabbitMqConsumerBufferCapacityTests
{
    private const string ConsumerTag = "ctag-1";
    private const string Queue = "buffer-capacity-queue";

    // A validation case that is not rejected hangs in WaitToReadAsync; the limit turns that into a failure.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        internal required RabbitMqTransportAdapter Adapter { get; init; }

        internal required IChannel Channel { get; init; }

        internal required IConnection Connection { get; init; }

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

        var harness = new Harness { Adapter = adapter, Channel = channel, Connection = connection };

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

    private static async Task WaitForConsumerAsync(Harness harness)
    {
        while (harness.Consumer is null)
        {
            await Task.Yield();
        }
    }

    private static async Task DisposeQuietlyAsync(IAsyncEnumerator<InboundMessage> enumerator)
    {
        try
        {
            await enumerator.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected once the token is honoured before every read.
        }

        await enumerator.DisposeAsync();
    }

    private static async Task AssertRejectedBeforeOpeningChannelAsync(FlowControlOptions options)
    {
        Harness harness = CreateHarness();
        using var cts = new CancellationTokenSource(TestTimeout);

        Func<Task> act = async () =>
        {
            await using IAsyncEnumerator<InboundMessage> enumerator = harness.Adapter
                .ConsumeAsync(Queue, options, cts.Token)
                .GetAsyncEnumerator(cts.Token);
            await enumerator.MoveNextAsync();
        };

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await harness.Connection.DidNotReceive().CreateChannelAsync(
            Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>());
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(20, 5)]      // prefetch well above the queue capacity
    [InlineData(3, 1)]
    [InlineData(1500, 1000)] // the runner path: PrefetchCount above the default InternalQueueCapacity
    public async Task ConsumeAsync_PrefetchAboveQueueCapacity_BuffersEveryDeliveryWithoutNack(int prefetch, int capacity)
    {
        // Arrange
        Harness harness = CreateHarness();
        using var cts = new CancellationTokenSource(TestTimeout);
        IAsyncEnumerator<InboundMessage> enumerator = harness.Adapter
            .ConsumeAsync(
                Queue,
                new FlowControlOptions { MaxInFlightMessages = prefetch, InternalQueueCapacity = capacity },
                cts.Token)
            .GetAsyncEnumerator(cts.Token);
        ValueTask<bool> firstMove = enumerator.MoveNextAsync();
        await WaitForConsumerAsync(harness);

        // Act — slow consumer: nothing is read while the broker pushes a full prefetch window.
        for (ulong tag = 1; tag <= (ulong)prefetch; tag++)
        {
            await DeliverAsync(harness, tag);
        }

        // Assert — zero nacks. Before the fix the nack count was in the range prefetch-capacity-1 ..
        // prefetch-capacity (the exact value raced with the reader taking the first buffered item).
        await harness.Channel.DidNotReceive().BasicNackAsync(
            Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        (await firstMove).Should().BeTrue();
        var tags = new List<ulong> { enumerator.Current.DeliveryTag };
        enumerator.Current.Dispose();
        while (tags.Count < prefetch && await enumerator.MoveNextAsync())
        {
            tags.Add(enumerator.Current.DeliveryTag);
            enumerator.Current.Dispose();
        }

        tags.Should().Equal(Enumerable.Range(1, prefetch).Select(i => (ulong)i));

        await cts.CancelAsync();
        await DisposeQuietlyAsync(enumerator);
    }

    [Theory]
    [InlineData(BoundedChannelFullMode.DropWrite)]
    [InlineData(BoundedChannelFullMode.DropNewest)]
    [InlineData(BoundedChannelFullMode.DropOldest)]
    public Task ConsumeAsync_FullModeOtherThanWait_ThrowsBeforeOpeningChannel(BoundedChannelFullMode fullMode) =>
        AssertRejectedBeforeOpeningChannelAsync(new FlowControlOptions { FullMode = fullMode });

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public Task ConsumeAsync_NonPositiveMaxInFlightMessages_ThrowsBeforeOpeningChannel(int maxInFlight) =>
        AssertRejectedBeforeOpeningChannelAsync(new FlowControlOptions { MaxInFlightMessages = maxInFlight });

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public Task ConsumeAsync_NonPositiveInternalQueueCapacity_ThrowsBeforeOpeningChannel(int capacity) =>
        AssertRejectedBeforeOpeningChannelAsync(new FlowControlOptions { InternalQueueCapacity = capacity });

    [Fact]
    public async Task ConsumeAsync_StoppedWithBufferAboveOldQueueCapacity_RequeuesEveryBufferedDelivery()
    {
        // Arrange — prefetch 1500 on a 1000-entry configured capacity: the buffer is grown to hold 1500.
        const int prefetch = 1500;
        Harness harness = CreateHarness();
        using var cts = new CancellationTokenSource(TestTimeout);
        IAsyncEnumerator<InboundMessage> enumerator = harness.Adapter
            .ConsumeAsync(
                Queue,
                new FlowControlOptions { MaxInFlightMessages = prefetch, InternalQueueCapacity = 1000 },
                cts.Token)
            .GetAsyncEnumerator(cts.Token);
        ValueTask<bool> firstMove = enumerator.MoveNextAsync();
        await WaitForConsumerAsync(harness);

        for (ulong tag = 1; tag <= prefetch; tag++)
        {
            await DeliverAsync(harness, tag);
        }

        // Tag 1 is handed to the caller (in flight); tags 2..1500 stay buffered.
        (await firstMove).Should().BeTrue();
        enumerator.Current.DeliveryTag.Should().Be(1UL);

        // Act — stop the consumer; the shutdown drain hands the buffered deliveries back.
        await cts.CancelAsync();
        await DisposeQuietlyAsync(enumerator);

        // Assert
        await harness.Channel.Received(prefetch - 1).BasicNackAsync(
            Arg.Is<ulong>(tag => tag >= 2UL), false, true, Arg.Any<CancellationToken>());
        await harness.Channel.DidNotReceive().BasicNackAsync(
            1UL, Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await harness.Channel.Received(1).BasicNackAsync(
            1500UL, false, true, Arg.Any<CancellationToken>());
    }
}
