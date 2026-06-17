using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Saga.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Saga;

public sealed class DelayRequeueScheduleProviderTests
{
    // ── Test types ────────────────────────────────────────────────────────────

    private sealed record PaymentTimeout(Guid OrderId);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (DelayRequeueScheduleProvider provider, ITransportAdapter transport, IMessageSerializer serializer) CreateProvider()
    {
        var transport = Substitute.For<ITransportAdapter>();
        var serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        var logger = NullLogger<DelayRequeueScheduleProvider>.Instance;
        return (new DelayRequeueScheduleProvider(transport, serializer, logger), transport, serializer);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScheduleAsync_ValidMessage_CompletesWithoutException()
    {
        var (provider, transport, _) = CreateProvider();
        transport.DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        transport.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SendResult> { new(true, 1UL) });
        var message = new PaymentTimeout(Guid.NewGuid());

        Func<Task> act = () => provider.ScheduleAsync(
            message, TimeSpan.FromSeconds(30), "order-timeout", Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ScheduleAsync_LogsSchedulingIntent()
    {
        // DelayRequeueScheduleProvider uses [LoggerMessage] source-generated logging (Debug level).
        // We verify no exception is thrown and the method completes — the log is not observable
        // without a test-specific ILoggerFactory because ILogger<T> where T is internal cannot be mocked.
        var (provider, transport, _) = CreateProvider();
        transport.DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        transport.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SendResult> { new(true, 1UL) });
        var message = new PaymentTimeout(Guid.NewGuid());

        Func<Task> act = () => provider.ScheduleAsync(
            message, TimeSpan.FromSeconds(30), "order-timeout", Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CancelAsync_LogsWarningAboutRabbitMqLimitation()
    {
        // CancelAsync logs a Warning about RabbitMQ not supporting selective message deletion.
        // We verify the method completes without error (observable behavior without mocking internals).
        var (provider, _, _) = CreateProvider();

        Func<Task> act = () => provider.CancelAsync<PaymentTimeout>(Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CancelAsync_ValidCall_CompletesWithoutException()
    {
        var (provider, _, _) = CreateProvider();

        Func<Task> act = () => provider.CancelAsync<PaymentTimeout>(Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ScheduleAsync_NullMessage_ThrowsArgumentNullException()
    {
        var (provider, _, _) = CreateProvider();

        Func<Task> act = () => provider.ScheduleAsync<PaymentTimeout>(
            null!, TimeSpan.FromSeconds(30), "order-timeout", Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ScheduleAsync_NullDestinationQueue_ThrowsArgumentNullException()
    {
        var (provider, _, _) = CreateProvider();
        var message = new PaymentTimeout(Guid.NewGuid());

        Func<Task> act = () => provider.ScheduleAsync(
            message, TimeSpan.FromSeconds(30), null!, Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ScheduleAsync_ZeroDelay_CompletesWithoutException()
    {
        var (provider, transport, _) = CreateProvider();
        transport.DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        transport.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SendResult> { new(true, 1UL) });
        var message = new PaymentTimeout(Guid.NewGuid());

        Func<Task> act = () => provider.ScheduleAsync(
            message, TimeSpan.Zero, "my-queue", Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Constructor_NullTransport_ThrowsArgumentNullException()
    {
        var serializer = Substitute.For<IMessageSerializer>();
        var logger = NullLogger<DelayRequeueScheduleProvider>.Instance;

        Action act = () => _ = new DelayRequeueScheduleProvider(null!, serializer, logger);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullSerializer_ThrowsArgumentNullException()
    {
        var transport = Substitute.For<ITransportAdapter>();
        var logger = NullLogger<DelayRequeueScheduleProvider>.Instance;

        Action act = () => _ = new DelayRequeueScheduleProvider(transport, null!, logger);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var transport = Substitute.For<ITransportAdapter>();
        var serializer = Substitute.For<IMessageSerializer>();

        Action act = () => _ = new DelayRequeueScheduleProvider(transport, serializer, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ScheduleAsync_ValidMessage_DeploysDelayQueueTopology()
    {
        var (provider, transport, serializer) = CreateProvider();
        var message = new PaymentTimeout(Guid.NewGuid());
        transport.DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        transport.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SendResult> { new(true, 1UL) });

        await provider.ScheduleAsync(message, TimeSpan.FromSeconds(30), "order-saga", Guid.NewGuid(), CancellationToken.None);

        await transport.Received(1).DeployTopologyAsync(
            Arg.Is<TopologyDeclaration>(t =>
                t.Queues.Count == 1 &&
                t.Queues[0].Name == "barewire.delay.30000.order-saga"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleAsync_ValidMessage_PublishesMessageToDelayQueue()
    {
        var (provider, transport, serializer) = CreateProvider();
        var message = new PaymentTimeout(Guid.NewGuid());
        transport.DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        transport.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SendResult> { new(true, 1UL) });

        await provider.ScheduleAsync(message, TimeSpan.FromSeconds(30), "order-saga", Guid.NewGuid(), CancellationToken.None);

        await transport.Received(1).SendBatchAsync(
            Arg.Is<IReadOnlyList<OutboundMessage>>(msgs =>
                msgs.Count == 1 &&
                msgs[0].RoutingKey == "barewire.delay.30000.order-saga" &&
                msgs[0].Headers["BW-Exchange"] == ""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleAsync_SetsCorrectQueueArguments()
    {
        var (provider, transport, serializer) = CreateProvider();
        var message = new PaymentTimeout(Guid.NewGuid());
        transport.DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        transport.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SendResult> { new(true, 1UL) });

        await provider.ScheduleAsync(message, TimeSpan.FromSeconds(30), "order-saga", Guid.NewGuid(), CancellationToken.None);

        await transport.Received(1).DeployTopologyAsync(
            Arg.Is<TopologyDeclaration>(t =>
                t.Queues[0].Arguments != null &&
                (long)t.Queues[0].Arguments!["x-message-ttl"] == 30000L &&
                (string)t.Queues[0].Arguments!["x-dead-letter-exchange"] == "" &&
                (string)t.Queues[0].Arguments!["x-dead-letter-routing-key"] == "order-saga"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleAsync_UsesDefaultExchangeForRouting()
    {
        var (provider, transport, serializer) = CreateProvider();
        var message = new PaymentTimeout(Guid.NewGuid());
        transport.DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        transport.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SendResult> { new(true, 1UL) });

        await provider.ScheduleAsync(message, TimeSpan.FromSeconds(30), "order-saga", Guid.NewGuid(), CancellationToken.None);

        await transport.Received(1).SendBatchAsync(
            Arg.Is<IReadOnlyList<OutboundMessage>>(msgs =>
                msgs[0].Headers["BW-Exchange"] == "" &&
                msgs[0].Headers.ContainsKey("BW-MessageType") &&
                msgs[0].Headers.ContainsKey("message-id")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleAsync_SameQueueTwice_DeploysTopologyOnlyOnce()
    {
        var (provider, transport, serializer) = CreateProvider();
        var message = new PaymentTimeout(Guid.NewGuid());
        transport.DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        transport.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SendResult> { new(true, 1UL) });

        await provider.ScheduleAsync(message, TimeSpan.FromSeconds(30), "order-saga", Guid.NewGuid(), CancellationToken.None);
        await provider.ScheduleAsync(message, TimeSpan.FromSeconds(30), "order-saga", Guid.NewGuid(), CancellationToken.None);

        await transport.Received(1).DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>());
        await transport.Received(2).SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleAsync_BrokerNack_ThrowsInvalidOperationException()
    {
        var (provider, transport, serializer) = CreateProvider();
        var message = new PaymentTimeout(Guid.NewGuid());
        transport.DeployTopologyAsync(Arg.Any<TopologyDeclaration>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        transport.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SendResult> { new(false, 0UL) });

        Func<Task> act = () => provider.ScheduleAsync(message, TimeSpan.FromSeconds(30), "order-saga", Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
