#pragma warning disable CA2012 // NSubstitute .Returns()/Received on ValueTask is a known false positive
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using BareWire.Observability;
using BareWire.Transport.RabbitMQ.Internal;
using BareWire.UnitTests.Core.Bus;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Guards the health signal of a RabbitMQ consumer that waits for automatic recovery: after a recoverable channel
/// shutdown the consumer remembers since when it waits, the wait ends on <c>consume-ok</c> (the client re-registers
/// the same consumer object) or on a fresh delivery, and the adapter reports the endpoint as
/// <see cref="BusStatus.Degraded"/> once the wait exceeds the recovery health timeout.
/// </summary>
public sealed class RabbitMqConsumerRecoveryHealthTests
{
    private const string Tag = "ctag";
    private const string Queue = "orders";
    private static readonly TimeSpan Timeout30s = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IChannel CreateChannel()
    {
        IChannel channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(true);
        channel.BasicNackAsync(Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ValueTask.CompletedTask);
        return channel;
    }

    private sealed class RecordedEvents
    {
        public List<TimeSpan> Overdue { get; } = [];

        public List<TimeSpan> Recovered { get; } = [];
    }

    private static RabbitMqConsumer CreateConsumer(
        FakeTimeProvider time,
        RecordedEvents? events = null,
        Func<bool>? connectionProbe = null,
        bool recovery = true,
        string endpointName = Queue) =>
        new(CreateChannel(), Channel.CreateBounded<InboundMessage>(8), new RabbitMqHeaderMapper(), "c1")
        {
            AssignedTag = Tag,
            EndpointName = endpointName,
            AutomaticRecoveryEnabled = recovery,
            IsConnectionOpen = connectionProbe ?? (() => false),
            TimeProvider = time,
            RecoveryHealthTimeout = Timeout30s,
            RecoveryOverdue = events is null ? null : (_, waited) => events.Overdue.Add(waited),
            RecoveredAfterOverdue = events is null ? null : (_, waited) => events.Recovered.Add(waited),
        };

    private static ShutdownEventArgs Shutdown(
        ShutdownInitiator initiator = ShutdownInitiator.Peer,
        ushort replyCode = 320) =>
        new(initiator, replyCode, "CONNECTION_FORCED");

    private static Task DeliverAsync(RabbitMqConsumer consumer, ulong tag) =>
        consumer.HandleBasicDeliverAsync(
            Tag,
            tag,
            redelivered: false,
            exchange: string.Empty,
            routingKey: "rk",
            Substitute.For<IReadOnlyBasicProperties>(),
            new byte[] { 1, 2, 3 });

    // ── Consumer state ────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleChannelShutdownAsync_RecoveryExpected_StartsWaitingForRecovery()
    {
        // Arrange
        var time = new FakeTimeProvider();
        RabbitMqConsumer consumer = CreateConsumer(time);

        // Act
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(TimeSpan.FromSeconds(7));

        // Assert
        consumer.TryGetRecoveryWait(out TimeSpan waited).Should().BeTrue();
        waited.Should().Be(TimeSpan.FromSeconds(7));
        consumer.IsRecoveryOverdue(out _).Should().BeFalse();
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_WaitLongerThanTimeout_IsOverdue()
    {
        // Arrange
        var time = new FakeTimeProvider();
        RabbitMqConsumer consumer = CreateConsumer(time);

        // Act
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(Timeout30s);

        // Assert
        consumer.IsRecoveryOverdue(out TimeSpan waited).Should().BeTrue();
        waited.Should().Be(Timeout30s);
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_Terminal_DoesNotWait()
    {
        // Arrange — the connection is still open, so the channel closed on its own and will not be recovered.
        var time = new FakeTimeProvider();
        RabbitMqConsumer consumer = CreateConsumer(time, connectionProbe: () => true);

        // Act
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(TimeSpan.FromMinutes(5));

        // Assert
        consumer.TryGetRecoveryWait(out _).Should().BeFalse();
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_RecoveryDisabled_DoesNotWait()
    {
        // Arrange
        var time = new FakeTimeProvider();
        RabbitMqConsumer consumer = CreateConsumer(time, recovery: false);

        // Act
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(TimeSpan.FromMinutes(5));

        // Assert
        consumer.TryGetRecoveryWait(out _).Should().BeFalse();
    }

    [Fact]
    public async Task HandleChannelShutdownAsync_SecondShutdown_KeepsFirstTimestamp()
    {
        // Arrange
        var time = new FakeTimeProvider();
        RabbitMqConsumer consumer = CreateConsumer(time);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(TimeSpan.FromSeconds(20));

        // Act
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(TimeSpan.FromSeconds(15));

        // Assert
        consumer.IsRecoveryOverdue(out TimeSpan waited).Should().BeTrue();
        waited.Should().Be(TimeSpan.FromSeconds(35));
    }

    [Fact]
    public async Task HandleBasicConsumeOkAsync_AfterShutdown_EndsWait()
    {
        // Arrange
        var time = new FakeTimeProvider();
        RabbitMqConsumer consumer = CreateConsumer(time);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(TimeSpan.FromSeconds(3));

        // Act — the client re-registers this consumer object after recovery.
        await consumer.HandleBasicConsumeOkAsync(Tag);
        time.Advance(TimeSpan.FromMinutes(5));

        // Assert
        consumer.TryGetRecoveryWait(out _).Should().BeFalse();
        consumer.ConsumerTags.Should().Contain(Tag, "the base implementation must still record the tag");
    }

    [Fact]
    public async Task HandleBasicDeliverAsync_FreshDelivery_EndsWait()
    {
        // Arrange
        var time = new FakeTimeProvider();
        RabbitMqConsumer consumer = CreateConsumer(time);
        await DeliverAsync(consumer, 1);
        await DeliverAsync(consumer, 2);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());

        // Act — the recovered channel offsets its tags above every tag of the dead channel.
        await DeliverAsync(consumer, 3);
        time.Advance(TimeSpan.FromMinutes(5));

        // Assert
        consumer.TryGetRecoveryWait(out _).Should().BeFalse();
    }

    [Fact]
    public async Task HandleBasicDeliverAsync_StaleDelivery_KeepsWaiting()
    {
        // Arrange
        var time = new FakeTimeProvider();
        RabbitMqConsumer consumer = CreateConsumer(time);
        await DeliverAsync(consumer, 1);
        await DeliverAsync(consumer, 2);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());

        // Act — a late delivery of the dead channel proves nothing about recovery.
        await DeliverAsync(consumer, 2);
        time.Advance(Timeout30s);

        // Assert
        consumer.IsRecoveryOverdue(out _).Should().BeTrue();
    }

    [Fact]
    public async Task RequestStop_WhileWaiting_EndsWait()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var events = new RecordedEvents();
        RabbitMqConsumer consumer = CreateConsumer(time, events);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());

        // Act
        consumer.RequestStop();
        time.Advance(TimeSpan.FromMinutes(5));

        // Assert — a stopping endpoint is not stuck, and the timer no longer fires.
        consumer.TryGetRecoveryWait(out _).Should().BeFalse();
        events.Overdue.Should().BeEmpty();
    }

    [Fact]
    public async Task StopRecoveryTracking_WhileWaiting_EndsWaitWithoutEvents()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var events = new RecordedEvents();
        RabbitMqConsumer consumer = CreateConsumer(time, events);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(Timeout30s);

        // Act
        consumer.StopRecoveryTracking();
        time.Advance(TimeSpan.FromMinutes(5));

        // Assert
        consumer.TryGetRecoveryWait(out _).Should().BeFalse();
        events.Overdue.Should().ContainSingle();
        events.Recovered.Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalShutdown_AfterRecoverableShutdown_EndsWait()
    {
        // Arrange
        var time = new FakeTimeProvider();
        bool connectionOpen = false;
        RabbitMqConsumer consumer = CreateConsumer(time, connectionProbe: () => connectionOpen);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());

        // Act — the recovered channel is later closed on its own; the stream ends, nothing is awaited any more.
        connectionOpen = true;
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(TimeSpan.FromMinutes(5));

        // Assert
        consumer.TryGetRecoveryWait(out _).Should().BeFalse();
    }

    // ── Overdue / recovered notifications ──────────────────────────────────────

    [Fact]
    public async Task Timer_WaitExceedsTimeout_RaisesOverdueOnceWithoutPolling()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var events = new RecordedEvents();
        RabbitMqConsumer consumer = CreateConsumer(time, events);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());

        // Act
        time.Advance(TimeSpan.FromSeconds(29));
        int beforeTimeout = events.Overdue.Count;
        time.Advance(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromMinutes(10));

        // Assert
        beforeTimeout.Should().Be(0);
        events.Overdue.Should().ContainSingle().Which.Should().Be(Timeout30s);
    }

    [Fact]
    public async Task ConsumeOk_BeforeTimeout_RaisesNoEvents()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var events = new RecordedEvents();
        RabbitMqConsumer consumer = CreateConsumer(time, events);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(TimeSpan.FromSeconds(5));

        // Act
        await consumer.HandleBasicConsumeOkAsync(Tag);
        time.Advance(TimeSpan.FromMinutes(5));

        // Assert
        events.Overdue.Should().BeEmpty();
        events.Recovered.Should().BeEmpty();
    }

    [Fact]
    public async Task ConsumeOk_AfterTimeout_RaisesRecoveredWithTotalWait()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var events = new RecordedEvents();
        RabbitMqConsumer consumer = CreateConsumer(time, events);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(TimeSpan.FromSeconds(45));

        // Act
        await consumer.HandleBasicConsumeOkAsync(Tag);

        // Assert
        events.Overdue.Should().ContainSingle();
        events.Recovered.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task NewShutdownSeries_AfterRecovery_RaisesOverdueAgain()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var events = new RecordedEvents();
        RabbitMqConsumer consumer = CreateConsumer(time, events);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(TimeSpan.FromSeconds(40));
        await consumer.HandleBasicConsumeOkAsync(Tag);

        // Act
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        time.Advance(TimeSpan.FromSeconds(40));

        // Assert
        events.Overdue.Should().HaveCount(2);
    }

    // ── Adapter health source ─────────────────────────────────────────────────

    private static RabbitMqTransportAdapter CreateAdapter(
        FakeTimeProvider time,
        ILogger<RabbitMqTransportAdapter>? logger = null,
        TimeSpan? recoveryHealthTimeout = null,
        IConnection? connection = null)
    {
        var adapter = new RabbitMqTransportAdapter(
            new RabbitMqTransportOptions
            {
                ConnectionString = "amqp://guest:guest@localhost:5672/",
                RecoveryHealthTimeout = recoveryHealthTimeout,
            },
            logger ?? new CapturingLogger<RabbitMqTransportAdapter>(),
            timeProvider: time);

        if (connection is not null)
        {
            // EnsureConnectedAsync returns early when _connection is open, so no broker connection is attempted.
            typeof(RabbitMqTransportAdapter)
                .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(adapter, connection);
        }

        return adapter;
    }

    private static ConcurrentDictionary<string, RabbitMqConsumer> GetConsumers(RabbitMqTransportAdapter adapter) =>
        (ConcurrentDictionary<string, RabbitMqConsumer>)typeof(RabbitMqTransportAdapter)
            .GetField("_activeConsumers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;

    [Fact]
    public void GetHealth_NoConsumers_ReturnsHealthyWithNoEndpoints()
    {
        // Arrange
        var time = new FakeTimeProvider();
        RabbitMqTransportAdapter adapter = CreateAdapter(time);

        // Act
        BusHealthStatus health = ((ITransportHealthSource)adapter).GetHealth();

        // Assert
        health.Status.Should().Be(BusStatus.Healthy);
        health.Endpoints.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHealth_WaitShorterThanTimeout_ReturnsHealthy()
    {
        // Arrange
        var time = new FakeTimeProvider();
        RabbitMqTransportAdapter adapter = CreateAdapter(time);
        RabbitMqConsumer consumer = CreateConsumer(time);
        GetConsumers(adapter)["c1"] = consumer;
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());

        // Act
        time.Advance(TimeSpan.FromSeconds(29));
        BusHealthStatus health = ((ITransportHealthSource)adapter).GetHealth();

        // Assert — a slow but still possible recovery is not an alarm.
        health.Status.Should().Be(BusStatus.Healthy);
        health.Endpoints.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHealth_OverdueWithConnectionClosed_ReportsEndpointDegraded()
    {
        // Arrange
        var time = new FakeTimeProvider();
        RabbitMqTransportAdapter adapter = CreateAdapter(time);
        RabbitMqConsumer consumer = CreateConsumer(time, connectionProbe: () => false);
        GetConsumers(adapter)["c1"] = consumer;
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());

        // Act
        time.Advance(TimeSpan.FromSeconds(42));
        BusHealthStatus health = ((ITransportHealthSource)adapter).GetHealth();

        // Assert
        health.Status.Should().Be(BusStatus.Degraded);
        EndpointHealthStatus endpoint = health.Endpoints.Should().ContainSingle().Subject;
        endpoint.EndpointName.Should().Be(Queue);
        endpoint.Status.Should().Be(BusStatus.Degraded);
        endpoint.Description.Should().Contain("42 s").And.Contain("connection has not been re-established");
        health.Description.Should().Contain("automatic recovery");
    }

    [Fact]
    public async Task GetHealth_OverdueWithConnectionOpen_ReportsConsumerNotReRegistered()
    {
        // Arrange — the recoverable shutdown happened while the connection was down; it is open again now.
        var time = new FakeTimeProvider();
        bool connectionOpen = false;
        RabbitMqTransportAdapter adapter = CreateAdapter(time);
        RabbitMqConsumer consumer = CreateConsumer(time, connectionProbe: () => connectionOpen);
        GetConsumers(adapter)["c1"] = consumer;
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        connectionOpen = true;

        // Act
        time.Advance(TimeSpan.FromMinutes(1));
        BusHealthStatus health = ((ITransportHealthSource)adapter).GetHealth();

        // Assert
        health.Endpoints.Should().ContainSingle().Which.Description
            .Should().Contain("consumer has not been re-registered");
    }

    [Fact]
    public async Task GetHealth_ProbeThrows_DoesNotThrowAndReportsConnectionNotReEstablished()
    {
        // Arrange
        var time = new FakeTimeProvider();
        bool throwing = false;
        RabbitMqTransportAdapter adapter = CreateAdapter(time);
        RabbitMqConsumer consumer = CreateConsumer(
            time,
            connectionProbe: () => throwing ? throw new ObjectDisposedException("connection") : false);
        GetConsumers(adapter)["c1"] = consumer;
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());
        throwing = true;

        // Act
        time.Advance(TimeSpan.FromMinutes(1));
        Func<BusHealthStatus> act = () => ((ITransportHealthSource)adapter).GetHealth();

        // Assert
        BusHealthStatus health = act.Should().NotThrow().Subject;
        health.Status.Should().Be(BusStatus.Degraded);
        health.Endpoints.Should().ContainSingle().Which.Description
            .Should().Contain("connection has not been re-established");
    }

    [Fact]
    public async Task GetHealth_OnlyOverdueConsumersAreReported()
    {
        // Arrange
        var time = new FakeTimeProvider();
        RabbitMqTransportAdapter adapter = CreateAdapter(time);
        RabbitMqConsumer stuck = CreateConsumer(time, endpointName: "stuck");
        RabbitMqConsumer healthy = CreateConsumer(time, endpointName: "healthy");
        GetConsumers(adapter)["c1"] = stuck;
        GetConsumers(adapter)["c2"] = healthy;
        await stuck.HandleChannelShutdownAsync(stuck.Channel, Shutdown());
        await healthy.HandleChannelShutdownAsync(healthy.Channel, Shutdown());
        await healthy.HandleBasicConsumeOkAsync(Tag);

        // Act
        time.Advance(TimeSpan.FromMinutes(1));
        BusHealthStatus health = ((ITransportHealthSource)adapter).GetHealth();

        // Assert
        health.Endpoints.Should().ContainSingle().Which.EndpointName.Should().Be("stuck");
    }

    [Fact]
    public async Task GetHealth_Description_ContainsOnlyMetadata()
    {
        // Arrange
        var time = new FakeTimeProvider();
        RabbitMqTransportAdapter adapter = CreateAdapter(time);
        RabbitMqConsumer consumer = CreateConsumer(time);
        GetConsumers(adapter)["c1"] = consumer;
        await DeliverAsync(consumer, 7);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());

        // Act
        time.Advance(TimeSpan.FromMinutes(1));
        BusHealthStatus health = ((ITransportHealthSource)adapter).GetHealth();

        // Assert — no consumer tag, routing key, delivery tag or connection string.
        string all = health.Description + " " + string.Join(" ", health.Endpoints.Select(e => e.Description));
        all.Should().NotContain(Tag).And.NotContain("rk").And.NotContain("guest").And.NotContain("amqp");
    }

    [Fact]
    public async Task BareWireHealthCheck_OverdueConsumer_ReportsDegradedWithEndpointName()
    {
        // Arrange — the bus control consults the real RabbitMQ health source through a forwarding adapter.
        var time = new FakeTimeProvider();
        RabbitMqTransportAdapter rabbit = CreateAdapter(time);
        RabbitMqConsumer consumer = CreateConsumer(time);
        GetConsumers(rabbit)["c1"] = consumer;
        ITransportAdapter adapter = Substitute.For<ITransportAdapter, ITransportHealthSource>();
        ((ITransportHealthSource)adapter).GetHealth().Returns(_ => ((ITransportHealthSource)rabbit).GetHealth());
        var (control, _) = BareWireBusControlHealthSourceTests.CreateControl(adapter);
        var sut = new BareWireHealthCheck(control);
        await consumer.HandleChannelShutdownAsync(consumer.Channel, Shutdown());

        // Act
        HealthCheckResult before = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        HealthCheckResult after = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        // Assert
        before.Status.Should().Be(HealthStatus.Healthy);
        after.Status.Should().Be(HealthStatus.Degraded);
        after.Description.Should().Contain("waiting for automatic recovery");
        after.Data.Should().ContainKey("endpoints");
        control.CheckHealth().Endpoints.Should().ContainSingle(e => e.EndpointName == Queue && e.Status == BusStatus.Degraded);
    }

    [Fact]
    public async Task ConsumeAsync_ConsumerCreated_ReceivesRecoveryHealthSettingsAndLogsTransitions()
    {
        // Arrange — basic.consume parks on a gate so the consumer stays registered while it is inspected.
        var time = new FakeTimeProvider();
        var logger = new CapturingLogger<RabbitMqTransportAdapter>();
        IChannel channel = CreateChannel();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.BasicQosAsync(Arg.Any<uint>(), Arg.Any<ushort>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        channel.BasicConsumeAsync(
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<IAsyncBasicConsumer>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);
        IConnection connection = Substitute.For<IConnection>();
        connection.IsOpen.Returns(true);
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));
        RabbitMqTransportAdapter adapter = CreateAdapter(
            time, logger, recoveryHealthTimeout: TimeSpan.FromSeconds(12), connection: connection);
        using var cts = new CancellationTokenSource(TestTimeout);
        await using IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(Queue, new FlowControlOptions(), cts.Token).GetAsyncEnumerator(cts.Token);
        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        while (GetConsumers(adapter).IsEmpty)
        {
            await Task.Delay(10, cts.Token);
        }

        RabbitMqConsumer consumer = GetConsumers(adapter).Values.Single();

        // Act — the connection drops, the wait exceeds the timeout, then the consumer is re-registered.
        connection.IsOpen.Returns(false);
        await consumer.HandleChannelShutdownAsync(channel, Shutdown());
        time.Advance(TimeSpan.FromSeconds(12));
        time.Advance(TimeSpan.FromSeconds(3));
        await consumer.HandleBasicConsumeOkAsync(Tag);

        // Assert
        consumer.TimeProvider.Should().BeSameAs(time);
        consumer.RecoveryHealthTimeout.Should().Be(TimeSpan.FromSeconds(12));
        logger.Events.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain(Queue).And.Contain("12");
        logger.Events.Where(e => e.Level == LogLevel.Information).Should().ContainSingle()
            .Which.Message.Should().Contain(Queue).And.Contain("15");

        // Cleanup — release the parked basic.consume and stop the consumer.
        await cts.CancelAsync();
        gate.TrySetResult(Tag);
        Func<Task> drain = () => moveNext;
        await drain.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Minimal logger that captures emitted log events for assertion.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message)> _events = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _events.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
