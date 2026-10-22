#pragma warning disable CA2012 // NSubstitute .Returns()/Received on ValueTask is a known false positive
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Internal;
using BareWire.UnitTests.Transport.InMemory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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

    // Real-time guard for the hang tests: the adapter's budgets run on a FakeTimeProvider, so a passing test
    // finishes in milliseconds; this bound only keeps a regression from hanging the whole test process.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

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

    private static Harness CreateHarness(
        ILogger<RabbitMqTransportAdapter>? logger = null, TimeProvider? timeProvider = null)
    {
        IChannel channel = CreateChannel();

        IConnection connection = Substitute.For<IConnection>();
        connection.IsOpen.Returns(true);
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));

        var adapter = new RabbitMqTransportAdapter(
            new RabbitMqTransportOptions { ConnectionString = "amqp://guest:guest@localhost:5672/" },
            logger ?? NullLogger<RabbitMqTransportAdapter>.Instance,
            timeProvider: timeProvider);

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

    // Advances the fake clock in 1 s steps until the task completes. Stepping (instead of one big jump) keeps the
    // test independent of when the adapter creates its timeout token: an Advance that lands before the token
    // exists is simply followed by the next one. Bounded by real time so a regression fails instead of hanging.
    private static async Task<bool> CompletesWhileAdvancingAsync(Task task, FakeTimeProvider time)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!task.IsCompleted && stopwatch.Elapsed < HangGuard)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        return task.IsCompleted;
    }

    // Same arrangement for every "close hangs" test: CloseAsync only finishes when the token it received is
    // cancelled (a token that can never be cancelled, such as None, leaves it pending forever).
    private static Task HangUntilCancelled(CancellationToken token) => Task.Delay(Timeout.Infinite, token);

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

    [Fact]
    public async Task ReleaseConsumerChannelAsync_WhenCloseHangsAndCallerPassesNone_CompletesWithinBudget()
    {
        // Arrange — the runner releases with CancellationToken.None; the transport must still bound the close.
        var time = new FakeTimeProvider();
        Harness harness = CreateHarness(timeProvider: time);
        IChannel hanging = CreateChannel();
        CancellationToken observed = default;
        hanging.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                observed = callInfo.ArgAt<CancellationToken>(3);
                return HangUntilCancelled(observed);
            });
        GetChannels(harness.Adapter)["hanging"] = hanging;

        // Act
        Task release = harness.Adapter.ReleaseConsumerChannelAsync("hanging", CancellationToken.None);
        bool completed = await CompletesWhileAdvancingAsync(release, time);

        // Assert
        completed.Should().BeTrue("the close must be bounded even though the caller passed CancellationToken.None");
        await release;
        observed.CanBeCanceled.Should().BeTrue();
        await hanging.Received(1).DisposeAsync();
        GetChannels(harness.Adapter).Should().NotContainKey("hanging");
    }

    [Fact]
    public async Task ReleaseConsumerChannelAsync_WhenCloseIgnoresItsToken_CompletesWithinBudget()
    {
        // Arrange — RabbitMQ.Client ignores the token of CloseAsync while the channel is still open, so the
        // bound cannot rely on the client honouring it.
        var time = new FakeTimeProvider();
        Harness harness = CreateHarness(timeProvider: time);
        IChannel hanging = CreateChannel();
        var neverCompletes = new TaskCompletionSource();
        hanging.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => neverCompletes.Task);
        GetChannels(harness.Adapter)["hanging"] = hanging;

        // Act
        Task release = harness.Adapter.ReleaseConsumerChannelAsync("hanging", CancellationToken.None);
        bool completed = await CompletesWhileAdvancingAsync(release, time);

        // Assert
        completed.Should().BeTrue();
        await release;
        await hanging.Received(1).DisposeAsync();
        GetChannels(harness.Adapter).Should().NotContainKey("hanging");
    }

    [Fact]
    public async Task ReleaseConsumerChannelAsync_WhenCloseHangs_LogsReleaseWarning()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger<RabbitMqTransportAdapter>();
        Harness harness = CreateHarness(logger, time);
        IChannel hanging = CreateChannel();
        hanging.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => HangUntilCancelled(callInfo.ArgAt<CancellationToken>(3)));
        GetChannels(harness.Adapter)["hanging"] = hanging;

        // Act
        Task release = harness.Adapter.ReleaseConsumerChannelAsync("hanging", CancellationToken.None);
        bool completed = await CompletesWhileAdvancingAsync(release, time);

        // Assert
        completed.Should().BeTrue();
        await release;
        IReadOnlyList<LogEntry> warnings = [.. logger.Entries.Where(e => e.Level == LogLevel.Warning)];
        warnings.Should().ContainSingle();
        warnings[0]["ChannelId"].Should().Be("hanging");
        warnings[0].Exception.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task ReleaseConsumerChannelAsync_WhenCallerTokenAlreadyCancelled_PassesCancelledToken()
    {
        // Arrange
        Harness harness = CreateHarness();
        IChannel channel = CreateChannel();
        bool cancelledAtCall = false;
        channel.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                CancellationToken token = callInfo.ArgAt<CancellationToken>(3);
                cancelledAtCall = token.IsCancellationRequested;
                return Task.FromCanceled(token);
            });
        GetChannels(harness.Adapter)["c"] = channel;

        // Act
        await harness.Adapter.ReleaseConsumerChannelAsync("c", new CancellationToken(canceled: true));

        // Assert — the caller's cancellation still reaches the close immediately.
        cancelledAtCall.Should().BeTrue();
        await channel.Received(1).DisposeAsync();
        GetChannels(harness.Adapter).Should().NotContainKey("c");
    }

    [Fact]
    public async Task ReleaseConsumerChannelAsync_WhenChannelDisposeHangs_CompletesWithinBudget()
    {
        // Arrange — IChannel.DisposeAsync() takes no token and, in the client, can wait on the broker.
        var time = new FakeTimeProvider();
        Harness harness = CreateHarness(timeProvider: time);
        IChannel channel = CreateChannel();
        var neverCompletes = new TaskCompletionSource();
        channel.DisposeAsync().Returns(callInfo => new ValueTask(neverCompletes.Task));
        GetChannels(harness.Adapter)["c"] = channel;

        // Act
        Task release = harness.Adapter.ReleaseConsumerChannelAsync("c", CancellationToken.None);
        bool completed = await CompletesWhileAdvancingAsync(release, time);

        // Assert
        completed.Should().BeTrue();
        await release;
        GetChannels(harness.Adapter).Should().NotContainKey("c");
    }

    [Fact]
    public async Task DisposeAsync_WhenConnectionCloseHangs_CompletesWithinBudgetAndDisposesConnection()
    {
        // Arrange
        var time = new FakeTimeProvider();
        Harness harness = CreateHarness(timeProvider: time);
        TimeSpan observedTimeout = TimeSpan.MaxValue;
        harness.Connection.CloseAsync(
                Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                observedTimeout = callInfo.ArgAt<TimeSpan>(2);
                return HangUntilCancelled(callInfo.ArgAt<CancellationToken>(4));
            });

        // Act
        Task dispose = harness.Adapter.DisposeAsync().AsTask();
        bool completed = await CompletesWhileAdvancingAsync(dispose, time);

        // Assert
        completed.Should().BeTrue();
        await dispose;
        observedTimeout.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(5));
        await harness.Connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenConnectionCloseIgnoresItsToken_CompletesWithinBudget()
    {
        // Arrange — RabbitMQ.Client replaces the token of an open connection's CloseAsync with None.
        var time = new FakeTimeProvider();
        Harness harness = CreateHarness(timeProvider: time);
        var neverCompletes = new TaskCompletionSource();
        harness.Connection.CloseAsync(
                Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => neverCompletes.Task);

        // Act
        Task dispose = harness.Adapter.DisposeAsync().AsTask();
        bool completed = await CompletesWhileAdvancingAsync(dispose, time);

        // Assert
        completed.Should().BeTrue();
        await dispose;
        await harness.Connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenConnectionDisposeHangs_CompletesWithinBudget()
    {
        // Arrange
        var time = new FakeTimeProvider();
        Harness harness = CreateHarness(timeProvider: time);
        var neverCompletes = new TaskCompletionSource();
        harness.Connection.DisposeAsync().Returns(callInfo => new ValueTask(neverCompletes.Task));

        // Act
        Task dispose = harness.Adapter.DisposeAsync().AsTask();
        bool completed = await CompletesWhileAdvancingAsync(dispose, time);

        // Assert
        completed.Should().BeTrue();
        await dispose;
    }

    [Fact]
    public async Task DisposeAsync_WhenConnectionDisposeThrows_DoesNotThrowAndLogsWarning()
    {
        // Arrange
        var logger = new CapturingLogger<RabbitMqTransportAdapter>();
        Harness harness = CreateHarness(logger);
        harness.Connection.DisposeAsync()
            .Returns(callInfo => ValueTask.FromException(new InvalidOperationException("dispose")));

        // Act
        Func<Task> act = async () => await harness.Adapter.DisposeAsync();

        // Assert
        await act.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }
}
