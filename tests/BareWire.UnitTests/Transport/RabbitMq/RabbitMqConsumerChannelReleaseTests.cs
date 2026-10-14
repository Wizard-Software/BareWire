#pragma warning disable CA2012 // NSubstitute .Returns()/Received on ValueTask is a known false positive
using System.Collections.Concurrent;
using System.Reflection;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Internal;
using BareWire.UnitTests.Transport.InMemory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Guards how consumer channels are released: the diagnostics logged when a release fails, the release of the
/// channel of a consumer that never received a delivery, the retention of the channel for settlement once a
/// delivery was handed out, and the resilience of the adapter's dispose against a failing channel.
/// </summary>
public sealed class RabbitMqConsumerChannelReleaseTests
{
    private const string ConsumerTag = "ctag-1";
    private const string ChannelIdHeader = "BW-ConsumerChannelId";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        internal required RabbitMqTransportAdapter Adapter { get; init; }

        internal required IChannel Channel { get; init; }

        internal required IConnection Connection { get; init; }

        internal IAsyncBasicConsumer? Consumer { get; set; }
    }

    private static IChannel CreateChannel()
    {
        IChannel channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(true);
        channel.BasicQosAsync(Arg.Any<uint>(), Arg.Any<ushort>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        channel.BasicCancelAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        channel.BasicNackAsync(Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ValueTask.CompletedTask);
        channel.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        channel.DisposeAsync().Returns(callInfo => ValueTask.CompletedTask);
        return channel;
    }

    private static Harness CreateHarness(ILogger<RabbitMqTransportAdapter>? logger = null)
    {
        IChannel channel = CreateChannel();

        IConnection connection = Substitute.For<IConnection>();
        connection.IsOpen.Returns(true);
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));

        var adapter = new RabbitMqTransportAdapter(
            new RabbitMqTransportOptions { ConnectionString = "amqp://guest:guest@localhost:5672/" },
            logger ?? NullLogger<RabbitMqTransportAdapter>.Instance);

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

    private static ConcurrentDictionary<string, IChannel> GetChannels(RabbitMqTransportAdapter adapter) =>
        (ConcurrentDictionary<string, IChannel>)typeof(RabbitMqTransportAdapter)
            .GetField("_activeConsumerChannels", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;

    private static ConcurrentDictionary<string, RabbitMqConsumer> GetConsumers(RabbitMqTransportAdapter adapter) =>
        (ConcurrentDictionary<string, RabbitMqConsumer>)typeof(RabbitMqTransportAdapter)
            .GetField("_activeConsumers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;

    private static Task AssertCloseCountAsync(IChannel channel, int expected) =>
        channel.Received(expected).CloseAsync(
            Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

    private static async Task WaitForConsumerAsync(Harness harness)
    {
        while (harness.Consumer is null)
        {
            await Task.Yield();
        }
    }

    private static Task DeliverAsync(Harness harness, ulong deliveryTag) =>
        harness.Consumer!.HandleBasicDeliverAsync(
            ConsumerTag,
            deliveryTag,
            redelivered: false,
            exchange: string.Empty,
            routingKey: "orders",
            properties: new BasicProperties { MessageId = $"m-{deliveryTag}" },
            body: new byte[] { 1, 2, 3 },
            cancellationToken: CancellationToken.None);

    // Starts the consumer, pushes one delivery and reads it; returns the pending enumerator's first message.
    private static async Task<InboundMessage> ReceiveOneAsync(Harness harness, IAsyncEnumerator<InboundMessage> enumerator)
    {
        ValueTask<bool> firstMove = enumerator.MoveNextAsync();
        await WaitForConsumerAsync(harness);
        await DeliverAsync(harness, 1);
        (await firstMove).Should().BeTrue();
        return enumerator.Current;
    }

    private static async Task StopQuietlyAsync(
        CancellationTokenSource cts, ValueTask<bool> pendingMove, IAsyncEnumerator<InboundMessage> enumerator)
    {
        await cts.CancelAsync();
        try
        {
            await pendingMove;
        }
        catch (OperationCanceledException)
        {
            // Expected: the token is honoured before every read.
        }

        await enumerator.DisposeAsync();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReleaseConsumerChannelAsync_WhenCloseFails_LogsEndpointNameAndChannelId()
    {
        // Arrange
        var logger = new CapturingLogger<RabbitMqTransportAdapter>();
        Harness harness = CreateHarness(logger);
        harness.Channel.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TimeoutException("close")));
        using var cts = new CancellationTokenSource(TestTimeout);
        await using IAsyncEnumerator<InboundMessage> enumerator = harness.Adapter
            .ConsumeAsync("orders", new FlowControlOptions(), cts.Token)
            .GetAsyncEnumerator(cts.Token);

        using InboundMessage message = await ReceiveOneAsync(harness, enumerator);
        string channelId = message.Headers[ChannelIdHeader];

        // Act
        await harness.Adapter.ReleaseConsumerChannelAsync(channelId);

        // Assert
        IReadOnlyList<LogEntry> warnings = [.. logger.Entries.Where(e => e.Level == LogLevel.Warning)];
        warnings.Should().ContainSingle();
        warnings[0]["EndpointName"].Should().Be("orders");
        warnings[0]["ChannelId"].Should().Be(channelId);
        await harness.Channel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ConsumeAsync_WhenStoppedBeforeAnyDelivery_ReleasesChannel()
    {
        // Arrange
        Harness harness = CreateHarness();
        using var cts = new CancellationTokenSource(TestTimeout);
        IAsyncEnumerator<InboundMessage> enumerator = harness.Adapter
            .ConsumeAsync("orders", new FlowControlOptions(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        ValueTask<bool> pendingMove = enumerator.MoveNextAsync();
        await WaitForConsumerAsync(harness);

        // Act — stop before any delivery arrives; basic.cancel succeeds.
        await StopQuietlyAsync(cts, pendingMove, enumerator);

        // Assert
        await AssertCloseCountAsync(harness.Channel, 1);
        await harness.Channel.Received(1).DisposeAsync();
        GetChannels(harness.Adapter).Should().BeEmpty();
        GetConsumers(harness.Adapter).Should().BeEmpty();
    }

    [Fact]
    public async Task ConsumeAsync_WhenStoppedAfterDelivery_KeepsChannelForSettlement()
    {
        // Arrange
        Harness harness = CreateHarness();
        using var cts = new CancellationTokenSource(TestTimeout);
        IAsyncEnumerator<InboundMessage> enumerator = harness.Adapter
            .ConsumeAsync("orders", new FlowControlOptions(), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        using InboundMessage message = await ReceiveOneAsync(harness, enumerator);

        // Act — stop after one delivery was handed out; the runner still has to settle it.
        await cts.CancelAsync();
        await enumerator.DisposeAsync();

        // Assert — the channel stays open and registered until the runner releases it.
        await AssertCloseCountAsync(harness.Channel, 0);
        await harness.Channel.Received(0).DisposeAsync();
        GetChannels(harness.Adapter).Should().ContainKey(message.Headers[ChannelIdHeader]);
    }

    [Fact]
    public async Task DisposeAsync_WhenChannelDisposeThrows_ReleasesRemainingChannelsAndConnection()
    {
        // Arrange — two registered channels whose DisposeAsync throws; enumeration order is not guaranteed,
        // so whichever channel is released first always throws.
        Harness harness = CreateHarness();
        IChannel first = CreateChannel();
        IChannel second = CreateChannel();
        first.DisposeAsync().Returns(callInfo => ValueTask.FromException(new InvalidOperationException("first")));
        second.DisposeAsync().Returns(callInfo => ValueTask.FromException(new InvalidOperationException("second")));
        ConcurrentDictionary<string, IChannel> channels = GetChannels(harness.Adapter);
        channels["first"] = first;
        channels["second"] = second;

        // Act
        Func<Task> act = async () => await harness.Adapter.DisposeAsync();

        // Assert
        await act.Should().NotThrowAsync();
        foreach (IChannel channel in new[] { first, second })
        {
            await AssertCloseCountAsync(channel, 1);
            await channel.Received(1).DisposeAsync();
        }

        await harness.Connection.Received(1).CloseAsync(
            Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await harness.Connection.Received(1).DisposeAsync();
        channels.Should().BeEmpty();
    }
}
