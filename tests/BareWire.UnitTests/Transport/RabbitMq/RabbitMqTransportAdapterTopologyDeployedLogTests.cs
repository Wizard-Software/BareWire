#pragma warning disable CA2012 // NSubstitute .Returns()/Received on ValueTask is a known false positive
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
/// Guards the operational <c>Information</c> entry the RabbitMQ adapter writes after a successful
/// <c>DeployTopologyAsync</c>: it carries only element counts (never names), is written for an empty topology
/// too, and is not written when the deployment fails or the caller cancels it.
/// </summary>
public sealed class RabbitMqTransportAdapterTopologyDeployedLogTests
{
    // Real-time guard: a passing test finishes in milliseconds; this bound only keeps a regression from
    // hanging the whole test process.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    private sealed class Harness
    {
        internal required RabbitMqTransportAdapter Adapter { get; init; }

        internal required IChannel Channel { get; init; }

        internal required CapturingLogger<RabbitMqTransportAdapter> Logger { get; init; }
    }

    private static Harness CreateHarness()
    {
        IChannel channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(true);
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
        var adapter = new RabbitMqTransportAdapter(
            new RabbitMqTransportOptions { ConnectionString = "amqp://guest:guest@localhost:5672/" },
            logger,
            timeProvider: new FakeTimeProvider())
        {
            CreateConnection = (_, _) => Task.FromResult(connection),
        };

        return new Harness { Adapter = adapter, Channel = channel, Logger = logger };
    }

    private static TopologyDeclaration FullTopology() =>
        new()
        {
            Exchanges =
            [
                new ExchangeDeclaration("orders", BwExchangeType.Topic),
                new ExchangeDeclaration("orders-audit", BwExchangeType.Fanout),
            ],
            Queues = [new QueueDeclaration("orders-queue")],
            ExchangeQueueBindings = [new ExchangeQueueBinding("orders", "orders-queue", "created")],
            ExchangeExchangeBindings = [new ExchangeExchangeBinding("orders", "orders-audit", "#")],
        };

    private static OperationInterruptedException BrokerNotFound() =>
        new(new ShutdownEventArgs(ShutdownInitiator.Peer, 404, "NOT_FOUND - no queue 'orders-queue'"));

    [Fact]
    public async Task DeployTopologyAsync_WhenDeploymentSucceeds_LogsTopologyDeployedWithElementCounts()
    {
        // Arrange
        Harness harness = CreateHarness();

        // Act
        await harness.Adapter.DeployTopologyAsync(FullTopology(), TestContext.Current.CancellationToken)
            .WaitAsync(Guard);

        // Assert
        LogEntry entry = harness.Logger.Entries.Should()
            .ContainSingle(e => e.Message.Contains("topology deployed", StringComparison.OrdinalIgnoreCase)).Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry["ExchangeCount"].Should().Be(2);
        entry["QueueCount"].Should().Be(1);
        entry["QueueBindingCount"].Should().Be(1);
        entry["ExchangeBindingCount"].Should().Be(1);
        entry.Message.Should().NotContain("orders");
        entry.Message.Should().NotContain("created");
        entry.State.Select(kv => kv.Key).Should().BeSubsetOf(
            ["ExchangeCount", "QueueCount", "QueueBindingCount", "ExchangeBindingCount", "{OriginalFormat}"]);
    }

    [Fact]
    public async Task DeployTopologyAsync_WhenDeclareFails_DoesNotLogTopologyDeployed()
    {
        // Arrange
        Harness harness = CreateHarness();
        harness.Channel.QueueDeclareAsync(
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<QueueDeclareOk>(BrokerNotFound()));

        // Act
        await Assert.ThrowsAsync<TopologyDeploymentException>(
            () => harness.Adapter.DeployTopologyAsync(FullTopology(), TestContext.Current.CancellationToken)
                .WaitAsync(Guard));

        // Assert
        harness.Logger.Entries.Should()
            .NotContain(e => e.Message.Contains("topology deployed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeployTopologyAsync_WhenCallerCancels_DoesNotLogTopologyDeployed()
    {
        // Arrange — the declaration cancels the caller's token, as in the channel clean-up tests.
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

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Adapter.DeployTopologyAsync(FullTopology(), cts.Token).WaitAsync(Guard));

        // Assert
        harness.Logger.Entries.Should()
            .NotContain(e => e.Message.Contains("topology deployed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeployTopologyAsync_WhenTopologyIsEmpty_LogsTopologyDeployedWithZeroCounts()
    {
        // Arrange
        Harness harness = CreateHarness();

        // Act
        await harness.Adapter.DeployTopologyAsync(new TopologyDeclaration(), TestContext.Current.CancellationToken)
            .WaitAsync(Guard);

        // Assert
        LogEntry entry = harness.Logger.Entries.Should()
            .ContainSingle(e => e.Message.Contains("topology deployed", StringComparison.OrdinalIgnoreCase)).Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry["ExchangeCount"].Should().Be(0);
        entry["QueueCount"].Should().Be(0);
        entry["QueueBindingCount"].Should().Be(0);
        entry["ExchangeBindingCount"].Should().Be(0);
    }
}
