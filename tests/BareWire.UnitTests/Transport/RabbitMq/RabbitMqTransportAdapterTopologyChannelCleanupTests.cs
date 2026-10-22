#pragma warning disable CA2012 // NSubstitute .Returns()/Received on ValueTask is a known false positive
using System.Diagnostics;
using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.RabbitMQ;
using BareWire.UnitTests.Transport.InMemory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using BwExchangeType = BareWire.Abstractions.ExchangeType;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Guards the clean-up of the temporary channel used by <c>DeployTopologyAsync</c>: the channel is closed only
/// while it is open, with a limit of its own, always disposed, and a failure of either step is logged without
/// replacing the original exception or failing a successful deployment.
/// </summary>
public sealed class RabbitMqTransportAdapterTopologyChannelCleanupTests
{
    // Real-time guard: the adapter's budgets run on a FakeTimeProvider, so a passing test finishes in
    // milliseconds; this bound only keeps a regression from hanging the whole test process.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        internal required RabbitMqTransportAdapter Adapter { get; init; }

        internal required IChannel Channel { get; init; }

        internal required CapturingLogger<RabbitMqTransportAdapter> Logger { get; init; }

        internal required FakeTimeProvider Time { get; init; }

        internal required HarnessState State { get; init; }
    }

    private static Harness CreateHarness()
    {
        var state = new HarnessState();
        IChannel channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(_ => state.Open);
        channel.ExchangeDeclareAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        channel.QueueDeclareAsync(
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new QueueDeclareOk("orders-queue", 0, 0)));
        channel.QueueBindAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        channel.ExchangeBindAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        channel.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        channel.DisposeAsync().Returns(_ => ValueTask.CompletedTask);

        IConnection connection = Substitute.For<IConnection>();
        connection.IsOpen.Returns(true);
        connection.Endpoint.Returns(new AmqpTcpEndpoint("localhost", 5672));
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));
        connection.CloseAsync(
                Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        connection.DisposeAsync().Returns(_ => ValueTask.CompletedTask);

        var logger = new CapturingLogger<RabbitMqTransportAdapter>();
        var time = new FakeTimeProvider();
        var adapter = new RabbitMqTransportAdapter(
            new RabbitMqTransportOptions { ConnectionString = "amqp://guest:guest@localhost:5672/" },
            logger,
            timeProvider: time)
        {
            CreateConnection = (_, _) => Task.FromResult(connection),
        };

        return new Harness { Adapter = adapter, Channel = channel, Logger = logger, Time = time, State = state };
    }

    // Holds the mutable IsOpen flag the channel substitute reads.
    private sealed class HarnessState
    {
        internal bool Open { get; set; } = true;
    }

    private static TopologyDeclaration ExchangeOnlyTopology() =>
        new() { Exchanges = [new ExchangeDeclaration("orders", BwExchangeType.Topic)] };

    private static TopologyDeclaration FullTopology() =>
        new()
        {
            Exchanges = [new ExchangeDeclaration("orders", BwExchangeType.Topic)],
            Queues = [new QueueDeclaration("orders-queue")],
            ExchangeQueueBindings = [new ExchangeQueueBinding("orders", "orders-queue", "created")],
            ExchangeExchangeBindings = [new ExchangeExchangeBinding("orders", "orders-audit", "#")],
        };

    private static OperationInterruptedException BrokerNotFound() =>
        new(new ShutdownEventArgs(ShutdownInitiator.Peer, 404, "NOT_FOUND - no exchange 'orders'"));

    private static AlreadyClosedException ChannelAlreadyClosed() =>
        new(new ShutdownEventArgs(ShutdownInitiator.Peer, 504, "CHANNEL_ERROR - channel is closed"));

    private static void DeclareExchangeThrows(IChannel channel, Exception exception) =>
        channel.ExchangeDeclareAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(exception));

    private static void CloseAsyncReturns(IChannel channel, Func<CancellationToken, Task> close) =>
        channel.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => close(callInfo.ArgAt<CancellationToken>(3)));

    private static Task AssertCloseCountAsync(IChannel channel, int expected) =>
        channel.Received(expected).CloseAsync(
            Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

    private static async Task<bool> CompletesWhileAdvancingAsync(Task task, FakeTimeProvider time)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!task.IsCompleted && stopwatch.Elapsed < Guard)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        return task.IsCompleted;
    }

    // CloseAsync only finishes when the token it received is cancelled (a token that can never be cancelled
    // leaves it pending forever).
    private static Task HangUntilCancelled(CancellationToken token) => Task.Delay(Timeout.Infinite, token);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeployTopologyAsync_WhenDeclareFailsAndCloseThrows_ThrowsTopologyDeploymentExceptionAndDisposesChannel()
    {
        // Arrange
        Harness harness = CreateHarness();
        DeclareExchangeThrows(harness.Channel, BrokerNotFound());
        CloseAsyncReturns(harness.Channel, _ => Task.FromException(ChannelAlreadyClosed()));

        // Act
        TopologyDeploymentException ex = await Assert.ThrowsAsync<TopologyDeploymentException>(
            () => harness.Adapter.DeployTopologyAsync(ExchangeOnlyTopology()).WaitAsync(Guard));

        // Assert
        ex.InnerException.Should().BeOfType<OperationInterruptedException>();
        await harness.Channel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task DeployTopologyAsync_WhenDeclareSucceedsAndCloseThrows_CompletesAndDisposesChannelAndLogsWarning()
    {
        // Arrange
        Harness harness = CreateHarness();
        CloseAsyncReturns(harness.Channel, _ => Task.FromException(ChannelAlreadyClosed()));

        // Act
        await harness.Adapter.DeployTopologyAsync(ExchangeOnlyTopology()).WaitAsync(Guard);

        // Assert
        await harness.Channel.Received(1).DisposeAsync();
        harness.Logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Warning && e.Message.Contains("topology channel"));
    }

    [Fact]
    public async Task DeployTopologyAsync_WhenChannelAlreadyClosed_SkipsCloseAndDisposesChannel()
    {
        // Arrange — the broker closes the channel as part of the failure, so it is no longer open afterwards.
        Harness harness = CreateHarness();
        harness.Channel.ExchangeDeclareAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                harness.State.Open = false;
                return Task.FromException(BrokerNotFound());
            });
        CloseAsyncReturns(harness.Channel, _ => Task.FromException(ChannelAlreadyClosed()));

        // Act
        await Assert.ThrowsAsync<TopologyDeploymentException>(
            () => harness.Adapter.DeployTopologyAsync(ExchangeOnlyTopology()).WaitAsync(Guard));

        // Assert
        await AssertCloseCountAsync(harness.Channel, 0);
        await harness.Channel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task DeployTopologyAsync_WhenCallerCancelsDuringDeclare_RethrowsCancellationAndClosesWithOwnToken()
    {
        // Arrange — the declaration cancels the caller's token; the close records whether the token it got is cancelled.
        Harness harness = CreateHarness();
        using var cts = new CancellationTokenSource();
        harness.Channel.ExchangeDeclareAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return Task.FromCanceled(cts.Token);
            });
        bool closeTokenCancelled = true;
        CloseAsyncReturns(harness.Channel, token =>
        {
            closeTokenCancelled = token.IsCancellationRequested;
            return token.IsCancellationRequested ? Task.FromException(new OperationCanceledException(token)) : Task.CompletedTask;
        });

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Adapter.DeployTopologyAsync(ExchangeOnlyTopology(), cts.Token).WaitAsync(Guard));

        // Assert
        closeTokenCancelled.Should().BeFalse("the close uses its own budget, not the caller's token");
        await AssertCloseCountAsync(harness.Channel, 1);
        await harness.Channel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task DeployTopologyAsync_WhenCloseHangs_CompletesWithinBudgetAndDisposesChannel()
    {
        // Arrange
        Harness harness = CreateHarness();
        CloseAsyncReturns(harness.Channel, HangUntilCancelled);

        // Act
        Task deploy = harness.Adapter.DeployTopologyAsync(ExchangeOnlyTopology(), CancellationToken.None);
        bool completed = await CompletesWhileAdvancingAsync(deploy, harness.Time);

        // Assert
        completed.Should().BeTrue("the close must be bounded even though the caller passed CancellationToken.None");
        await deploy;
        await harness.Channel.Received(1).DisposeAsync();
        harness.Logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Warning && e.Message.Contains("topology channel"));
    }

    [Fact]
    public async Task DeployTopologyAsync_WhenDisposeHangs_CompletesWithinBudget()
    {
        // Arrange
        Harness harness = CreateHarness();
        var neverCompletes = new TaskCompletionSource();
        harness.Channel.DisposeAsync().Returns(_ => new ValueTask(neverCompletes.Task));

        // Act
        Task deploy = harness.Adapter.DeployTopologyAsync(ExchangeOnlyTopology(), CancellationToken.None);
        bool completed = await CompletesWhileAdvancingAsync(deploy, harness.Time);

        // Assert
        completed.Should().BeTrue("the dispose must be bounded");
        await deploy;
    }

    [Fact]
    public async Task DeployTopologyAsync_WhenAllSucceeds_DeclaresInOrderClosesOnceAndDisposesOnce()
    {
        // Arrange
        Harness harness = CreateHarness();

        // Act
        await harness.Adapter.DeployTopologyAsync(FullTopology()).WaitAsync(Guard);

        // Assert
        Received.InOrder(() =>
        {
            harness.Channel.ExchangeDeclareAsync(
                "orders", Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            harness.Channel.QueueDeclareAsync(
                "orders-queue", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            harness.Channel.QueueBindAsync(
                "orders-queue", "orders", "created",
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            harness.Channel.ExchangeBindAsync(
                "orders-audit", "orders", "#",
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            harness.Channel.CloseAsync(
                Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        });
        await AssertCloseCountAsync(harness.Channel, 1);
        await harness.Channel.Received(1).DisposeAsync();
        harness.Logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task DeployTopologyAsync_WhenQueueDeclareFails_MapsToTopologyDeploymentExceptionWithElementAndBrokerText()
    {
        // Arrange
        Harness harness = CreateHarness();
        harness.Channel.QueueDeclareAsync(
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<QueueDeclareOk>(BrokerNotFound()));

        // Act
        TopologyDeploymentException ex = await Assert.ThrowsAsync<TopologyDeploymentException>(
            () => harness.Adapter.DeployTopologyAsync(FullTopology()).WaitAsync(Guard));

        // Assert
        ex.TopologyElement.Should().Be("orders-queue");
        ex.BrokerError.Should().Contain("NOT_FOUND");
        await AssertCloseCountAsync(harness.Channel, 1);
        await harness.Channel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task DeployTopologyAsync_WhenAdapterDisposedDuringClose_AbandonsCloseAndDisposesChannel()
    {
        // Arrange — the close is in progress and only ends when its token is cancelled. The adapter's clock is a
        // FakeTimeProvider that is never advanced, so the 5-second budget cannot be what ends the close.
        Harness harness = CreateHarness();
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CloseAsyncReturns(harness.Channel, token =>
        {
            closeStarted.TrySetResult();
            return HangUntilCancelled(token);
        });

        Task deploy = harness.Adapter.DeployTopologyAsync(ExchangeOnlyTopology(), CancellationToken.None);
        await closeStarted.Task.WaitAsync(Guard);

        // Act
        await harness.Adapter.DisposeAsync();

        // Assert — the abandoned close is an expected outcome of the dispose, so it is not a warning.
        await deploy.WaitAsync(Guard);
        await harness.Channel.Received(1).DisposeAsync();
        harness.Logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        LogEntry abandoned = harness.Logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Debug && e.Message.Contains("topology channel")).Subject;
        abandoned.Exception.Should().BeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task DeployTopologyAsync_WhenAdapterDisposedBeforeClose_SkipsCloseAndDisposesChannel()
    {
        // Arrange — the adapter is disposed while the declaration is running, so it is already disposed when the
        // channel is cleaned up.
        Harness harness = CreateHarness();
        harness.Channel.ExchangeDeclareAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => harness.Adapter.DisposeAsync().AsTask());

        // Act
        await harness.Adapter.DeployTopologyAsync(ExchangeOnlyTopology()).WaitAsync(Guard);

        // Assert
        await AssertCloseCountAsync(harness.Channel, 0);
        await harness.Channel.Received(1).DisposeAsync();
    }
}
