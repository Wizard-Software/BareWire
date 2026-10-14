#pragma warning disable CA2012 // NSubstitute .Returns()/Received on ValueTask is a known false positive
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Guards the consumer start-up path of <see cref="RabbitMqTransportAdapter.ConsumeAsync"/>: whenever
/// <c>basic.qos</c> or <c>basic.consume</c> fails (or the adapter is disposed mid-setup), the freshly opened
/// consumer channel is closed, disposed and unregistered exactly once, buffered deliveries are returned to the
/// pool without a nack, and the original exception reaches the caller.
/// </summary>
public sealed class RabbitMqConsumerSetupFailureTests
{
    private const string ConsumerTag = "ctag-1";
    private const string Queue = "orders";

    // A setup failure that is not handled hangs in ReadUntilCancelledAsync; the limit turns that into a failure.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        internal required RabbitMqTransportAdapter Adapter { get; init; }

        internal required IChannel Channel { get; init; }

        internal required IConnection Connection { get; init; }
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
        channel.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        channel.DisposeAsync().Returns(callInfo => ValueTask.CompletedTask);
        channel.BasicConsumeAsync(
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<IAsyncBasicConsumer>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ConsumerTag));

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

        return new Harness { Adapter = adapter, Channel = channel, Connection = connection };
    }

    private static ConcurrentDictionary<string, IChannel> GetChannels(RabbitMqTransportAdapter adapter) =>
        (ConcurrentDictionary<string, IChannel>)typeof(RabbitMqTransportAdapter)
            .GetField("_activeConsumerChannels", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;

    private static ConcurrentDictionary<string, RabbitMqConsumer> GetConsumers(RabbitMqTransportAdapter adapter) =>
        (ConcurrentDictionary<string, RabbitMqConsumer>)typeof(RabbitMqTransportAdapter)
            .GetField("_activeConsumers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;

    private static void StubBasicQos(IChannel channel, Func<NSubstitute.Core.CallInfo, Task> behaviour) =>
        channel.BasicQosAsync(Arg.Any<uint>(), Arg.Any<ushort>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(behaviour);

    private static void StubBasicConsume(IChannel channel, Func<NSubstitute.Core.CallInfo, Task<string>> behaviour) =>
        channel.BasicConsumeAsync(
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<IAsyncBasicConsumer>(), Arg.Any<CancellationToken>())
            .Returns(behaviour);

    private static void StubClose(IChannel channel, Func<NSubstitute.Core.CallInfo, Task> behaviour) =>
        channel.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(behaviour);

    private static Task AssertCloseCountAsync(IChannel channel, int expected) =>
        channel.Received(expected).CloseAsync(
            Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

    private static async Task AssertReleasedAsync(IChannel channel, int closeCount = 1)
    {
        await AssertCloseCountAsync(channel, closeCount);
        await channel.Received(1).DisposeAsync();
    }

    private static IAsyncEnumerator<InboundMessage> Consume(Harness harness, CancellationToken token) =>
        harness.Adapter.ConsumeAsync(Queue, new FlowControlOptions(), token).GetAsyncEnumerator(token);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_WhenBasicQosThrows_ClosesAndDisposesChannelAndRethrows()
    {
        // Arrange
        Harness harness = CreateHarness();
        var failure = new InvalidOperationException("qos");
        StubBasicQos(harness.Channel, _ => Task.FromException(failure));
        using var cts = new CancellationTokenSource(TestTimeout);

        await using IAsyncEnumerator<InboundMessage> enumerator = Consume(harness, cts.Token);

        // Act
        Func<Task> act = async () => await enumerator.MoveNextAsync();

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        await AssertReleasedAsync(harness.Channel);
    }

    [Fact]
    public async Task ConsumeAsync_WhenBasicQosThrows_DoesNotLeaveChannelRegistered()
    {
        // Arrange
        Harness harness = CreateHarness();
        StubBasicQos(harness.Channel, _ => Task.FromException(new InvalidOperationException("qos")));
        using var cts = new CancellationTokenSource(TestTimeout);

        await using IAsyncEnumerator<InboundMessage> enumerator = Consume(harness, cts.Token);
        Func<Task> act = async () => await enumerator.MoveNextAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Act
        await harness.Adapter.DisposeAsync();

        // Assert — nothing left registered, and the adapter's dispose did not close the channel a second time.
        GetChannels(harness.Adapter).Should().BeEmpty();
        GetConsumers(harness.Adapter).Should().BeEmpty();
        await AssertReleasedAsync(harness.Channel);
    }

    [Fact]
    public async Task ConsumeAsync_WhenBasicConsumeThrows_ClosesDisposesAndUnregistersChannel()
    {
        // Arrange
        Harness harness = CreateHarness();
        var failure = new InvalidOperationException("consume");
        StubBasicConsume(harness.Channel, _ => Task.FromException<string>(failure));
        using var cts = new CancellationTokenSource(TestTimeout);

        await using IAsyncEnumerator<InboundMessage> enumerator = Consume(harness, cts.Token);

        // Act
        Func<Task> act = async () => await enumerator.MoveNextAsync();

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        await AssertReleasedAsync(harness.Channel);
        GetChannels(harness.Adapter).Should().BeEmpty();
        GetConsumers(harness.Adapter).Should().BeEmpty();
    }

    [Fact]
    public async Task ConsumeAsync_WhenBrokerClosesChannelOnBasicConsume_DisposesWithoutCloseAndUnregisters()
    {
        // Arrange — the broker closes the channel (e.g. 404 NOT_FOUND on the queue) while basic.consume is in flight.
        Harness harness = CreateHarness();
        bool isOpen = true;
        harness.Channel.IsOpen.Returns(_ => isOpen);
        var failure = new OperationInterruptedException(
            new ShutdownEventArgs(ShutdownInitiator.Peer, 404, "NOT_FOUND - no queue 'orders'"));
        StubBasicConsume(harness.Channel, _ =>
        {
            isOpen = false;
            return Task.FromException<string>(failure);
        });
        using var cts = new CancellationTokenSource(TestTimeout);

        await using IAsyncEnumerator<InboundMessage> enumerator = Consume(harness, cts.Token);

        // Act
        Func<Task> act = async () => await enumerator.MoveNextAsync();

        // Assert
        (await act.Should().ThrowAsync<OperationInterruptedException>()).Which.Should().BeSameAs(failure);
        await AssertReleasedAsync(harness.Channel, closeCount: 0);
        GetChannels(harness.Adapter).Should().BeEmpty();
        GetConsumers(harness.Adapter).Should().BeEmpty();
    }

    [Fact]
    public async Task ConsumeAsync_WhenChannelCloseFailsDuringCleanup_RethrowsOriginalException()
    {
        // Arrange
        Harness harness = CreateHarness();
        var failure = new InvalidOperationException("qos");
        StubBasicQos(harness.Channel, _ => Task.FromException(failure));
        StubClose(harness.Channel, _ => Task.FromException(new TimeoutException("close")));
        using var cts = new CancellationTokenSource(TestTimeout);

        await using IAsyncEnumerator<InboundMessage> enumerator = Consume(harness, cts.Token);

        // Act
        Func<Task> act = async () => await enumerator.MoveNextAsync();

        // Assert — the cleanup failure must not replace the original exception.
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        await harness.Channel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ConsumeAsync_WhenBasicQosCancelled_ReleasesChannelDespiteCancelledToken()
    {
        // Arrange
        Harness harness = CreateHarness();
        using var cts = new CancellationTokenSource(TestTimeout);
        CancellationToken callerToken = cts.Token;
        StubBasicQos(harness.Channel, _ =>
        {
            cts.Cancel();
            return Task.FromException(new OperationCanceledException(callerToken));
        });

        CancellationToken? cleanupToken = null;
        bool? cleanupTokenCancelledOnEntry = null;
        StubClose(harness.Channel, callInfo =>
        {
            cleanupToken = callInfo.ArgAt<CancellationToken>(3);
            cleanupTokenCancelledOnEntry = cleanupToken.Value.IsCancellationRequested;
            return Task.CompletedTask;
        });

        await using IAsyncEnumerator<InboundMessage> enumerator = Consume(harness, callerToken);

        // Act
        Func<Task> act = async () => await enumerator.MoveNextAsync();

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        await AssertReleasedAsync(harness.Channel);
        cleanupToken.Should().NotBeNull();
        cleanupToken!.Value.Should().NotBe(callerToken, "cleanup must not reuse the caller's (cancelled) token");
        cleanupToken.Value.Should().NotBe(CancellationToken.None, "cleanup must be bounded by its own timeout");
        cleanupTokenCancelledOnEntry.Should().BeFalse("the cleanup token must not be cancelled when CloseAsync starts");
    }

    [Fact]
    public async Task ConsumeAsync_WhenChannelDisposeFailsDuringCleanup_RethrowsOriginalException()
    {
        // Arrange
        Harness harness = CreateHarness();
        var failure = new InvalidOperationException("qos");
        StubBasicQos(harness.Channel, _ => Task.FromException(failure));
        harness.Channel.DisposeAsync()
            .Returns(callInfo => ValueTask.FromException(new ObjectDisposedException("channel")));
        using var cts = new CancellationTokenSource(TestTimeout);

        await using IAsyncEnumerator<InboundMessage> enumerator = Consume(harness, cts.Token);

        // Act
        Func<Task> act = async () => await enumerator.MoveNextAsync();

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        await harness.Channel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ConsumeAsync_WhenBasicConsumeThrowsAfterDelivery_DisposesBufferedDeliveriesWithoutNack()
    {
        // Arrange — a delivery arrives after consume-ok, then basic.consume's continuation fails.
        Harness harness = CreateHarness();
        RabbitMqConsumer? consumer = null;
        var failure = new InvalidOperationException("consume");
        StubBasicConsume(harness.Channel, async callInfo =>
        {
            consumer = (RabbitMqConsumer)callInfo.ArgAt<IAsyncBasicConsumer>(6);
            await consumer.HandleBasicDeliverAsync(
                ConsumerTag,
                deliveryTag: 1,
                redelivered: false,
                exchange: string.Empty,
                routingKey: Queue,
                properties: new BasicProperties { MessageId = "m-1" },
                body: new byte[] { 1, 2, 3 },
                cancellationToken: CancellationToken.None);
            throw failure;
        });
        using var cts = new CancellationTokenSource(TestTimeout);

        await using IAsyncEnumerator<InboundMessage> enumerator = Consume(harness, cts.Token);

        // Act
        Func<Task> act = async () => await enumerator.MoveNextAsync();

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        await harness.Channel.DidNotReceive().BasicNackAsync(
            Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await harness.Channel.DidNotReceive().BasicRejectAsync(
            Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await harness.Channel.DidNotReceive().BasicAckAsync(
            Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        consumer.Should().NotBeNull();
        var buffer = (Channel<InboundMessage>)typeof(RabbitMqConsumer)
            .GetField("_inboundChannel", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(consumer)!;
        buffer.Reader.TryPeek(out _).Should().BeFalse("buffered deliveries must be disposed, not left in the buffer");
        buffer.Reader.Completion.IsCompleted.Should().BeTrue("the drained, completed buffer must report completion");

        await AssertReleasedAsync(harness.Channel);
    }

    [Fact]
    public async Task ConsumeAsync_WhenAdapterDisposedBeforeRegistration_ReleasesChannelAndThrowsObjectDisposed()
    {
        // Arrange — the adapter is disposed while the channel is being created (sweep runs before registration).
        Harness harness = CreateHarness();
        harness.Connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await harness.Adapter.DisposeAsync();
                return harness.Channel;
            });
        using var cts = new CancellationTokenSource(TestTimeout);

        await using IAsyncEnumerator<InboundMessage> enumerator = Consume(harness, cts.Token);

        // Act
        Func<Task> act = async () => await enumerator.MoveNextAsync();

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>();
        await harness.Channel.DidNotReceive().BasicQosAsync(
            Arg.Any<uint>(), Arg.Any<ushort>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await AssertReleasedAsync(harness.Channel);
        GetChannels(harness.Adapter).Should().BeEmpty();
        GetConsumers(harness.Adapter).Should().BeEmpty();
    }

    [Fact]
    public async Task ConsumeAsync_WhenAdapterDisposedDuringBasicQos_ClosesChannelExactlyOnce()
    {
        // Arrange — the adapter is disposed while basic.qos is in flight; qos then fails like on a closed channel.
        Harness harness = CreateHarness();
        var failure = new InvalidOperationException("channel closed");
        StubBasicQos(harness.Channel, async _ =>
        {
            await harness.Adapter.DisposeAsync();
            throw failure;
        });
        using var cts = new CancellationTokenSource(TestTimeout);

        await using IAsyncEnumerator<InboundMessage> enumerator = Consume(harness, cts.Token);

        // Act
        Func<Task> act = async () => await enumerator.MoveNextAsync();

        // Assert — the sweep and the start-up cleanup must not both release the channel.
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        await AssertReleasedAsync(harness.Channel);
    }
}
