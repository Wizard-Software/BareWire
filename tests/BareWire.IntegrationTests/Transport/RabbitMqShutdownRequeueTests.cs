using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using Xunit;

// BareWire and RabbitMQ.Client both expose an ExchangeType; the topology API uses BareWire's.
using ExchangeType = BareWire.Abstractions.ExchangeType;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Measures what a real RabbitMQ broker does with the deliveries a consumer holds when its
/// <see cref="ReceiveEndpointRunner"/> is cancelled while a handler is blocked. The tests share one
/// scenario: five messages are published, the handler blocks on its cancellation token, the runner token
/// is cancelled once the prefetch buffer has filled, and the adapter is disposed so the broker hands back
/// everything that was never settled. The queue contents, the dead-letter queue and the per-message
/// <c>x-delivery-count</c> are then read back through a single <c>ConsumeAsync</c> stream with an ack for
/// each message. Every test uses unique queue and exchange names to prevent cross-test interference.
/// </summary>
public sealed class RabbitMqShutdownRequeueTests(AspireFixture fixture) : IClassFixture<AspireFixture>
{
    private const int MessageCount = 5;
    private const string DeliveryCountHeader = "x-delivery-count";
    private const string MsgIdHeader = "test-msg-id";

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Classic queue: the shutdown must not lose any message. Redelivery counts are not tracked by a classic
    /// queue, so this is a no-loss guard, not a spin detector.
    /// </summary>
    [Fact]
    public async Task RunAsync_ClassicQueueConsumerBlockedOnCancellation_LosesNoMessage()
    {
        ShutdownMeasurement result = await RunShutdownScenarioAsync(QueueKind.Classic);

        result.RemainingInQueue.Should().Be(MessageCount, "no message may be lost on shutdown ({0})", result);
        result.DeadLettered.Should().Be(0, "a shutdown must not dead-letter anything ({0})", result);
    }

    /// <summary>
    /// Quorum queue with <c>x-delivery-limit</c> and a DLX: every message may be returned to the queue at most
    /// once by a shutdown, and none may be dead-lettered.
    /// </summary>
    [Fact]
    public async Task RunAsync_QuorumQueueConsumerBlockedOnCancellation_RequeuesEachMessageAtMostOnce()
    {
        ShutdownMeasurement result = await RunShutdownScenarioAsync(QueueKind.Quorum);

        result.RemainingInQueue.Should().Be(MessageCount, "no message may be lost on shutdown ({0})", result);
        result.DeadLettered.Should().Be(0, "a shutdown must not exhaust the quorum delivery-limit ({0})", result);
        result.DeliveryCounts.Should().HaveCount(MessageCount, "every message must be readable again ({0})", result);
        result.DeliveryCounts.Max().Should().BeLessThanOrEqualTo(
            1, "each message is requeued at most once by a shutdown ({0})", result);
    }

    // ── Scenario ──────────────────────────────────────────────────────────────

    private async Task<ShutdownMeasurement> RunShutdownScenarioAsync(QueueKind kind)
    {
        string id = Guid.NewGuid().ToString("N");
        string srcQueue = $"test-shutdown-src-{id}";
        string dlxName = $"test-shutdown-dlx-{id}";
        string dlqName = $"test-shutdown-dlq-{id}";

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(90));
        CancellationToken ct = cts.Token;

        try
        {
            await using (RabbitMqTransportAdapter adapter = CreateAdapter())
            {
                await DeployTopologyAsync(adapter, kind, srcQueue, dlxName, dlqName, ct);

                for (int i = 0; i < MessageCount; i++)
                {
                    await PublishAsync(adapter, srcQueue, $"msg-{i}", ct);
                }

                var tracker = new BlockingTracker();
                ReceiveEndpointRunner runner = BuildRunner(srcQueue, dlxName, dlqName, adapter, tracker);

                using CancellationTokenSource runnerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task runTask = runner.RunAsync(runnerCts.Token);

                await tracker.FirstMessageReceived.WaitAsync(ct);

                // Let the broker push the rest of the messages into the adapter's prefetch buffer.
                await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);

                await runnerCts.CancelAsync();
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    // Expected: cooperative shutdown surfaces as cancellation.
                }
            }

            // The adapter is disposed: the broker returns everything that was never settled.
            ShutdownMeasurement measurement = await MeasureAsync(kind, srcQueue, dlqName, ct);
            TestContext.Current.SendDiagnosticMessage("Shutdown measurement: " + measurement);
            return measurement;
        }
        finally
        {
            await CleanUpAsync(srcQueue, dlqName, dlxName);
        }
    }

    private async Task<ShutdownMeasurement> MeasureAsync(
        QueueKind kind, string srcQueue, string dlqName, CancellationToken ct)
    {
        // Wait (bounded) for the broker to make the returned deliveries ready again.
        int remaining = await WaitForReadyCountAsync(srcQueue, MessageCount, TimeSpan.FromSeconds(5), ct);
        int deadLettered = await GetMessageCountAsync(dlqName, ct);

        // One ConsumeAsync stream; ack each message after recording its delivery count.
        var deliveryCounts = new List<int>();
        await using RabbitMqTransportAdapter reader = CreateAdapter();
        using CancellationTokenSource readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(10));
        FlowControlOptions flow = new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

        if (remaining > 0)
        {
            try
            {
                await foreach (InboundMessage msg in reader.ConsumeAsync(srcQueue, flow, readCts.Token))
                {
                    deliveryCounts.Add(ParseDeliveryCount(msg));
                    await reader.SettleAsync(SettlementAction.Ack, msg, CancellationToken.None);
                    if (deliveryCounts.Count >= remaining)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (readCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Read timeout: fewer messages than the queue count reported; surfaced via DeliveryCounts.Count.
            }
        }

        return new ShutdownMeasurement(kind, remaining, deadLettered, deliveryCounts);
    }

    // x-delivery-count: missing header means "never returned" (0); an unparseable value is a test failure.
    private static int ParseDeliveryCount(InboundMessage msg)
    {
        if (!msg.Headers.TryGetValue(DeliveryCountHeader, out string? raw))
        {
            return 0;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new InvalidOperationException($"Header '{DeliveryCountHeader}' is not an integer: '{raw}'.");
    }

    // ── Broker helpers ────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions { ConnectionString = fixture.GetRabbitMqConnectionString() },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    private static async Task DeployTopologyAsync(
        RabbitMqTransportAdapter adapter,
        QueueKind kind,
        string srcQueue,
        string dlxName,
        string dlqName,
        CancellationToken ct)
    {
        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(dlxName, ExchangeType.Direct, durable: true, autoDelete: false);
        configurator.DeclareQueue(dlqName, durable: true, autoDelete: false);
        configurator.BindExchangeToQueue(dlxName, dlqName, routingKey: dlqName);

        if (kind == QueueKind.Quorum)
        {
            // Quorum queues must be durable and non-exclusive; x-delivery-limit makes a requeue spin visible.
            configurator.DeclareQueue(srcQueue, durable: true, autoDelete: false, configure: q => q
                .SetQueueType(QueueType.Quorum)
                .Argument("x-delivery-limit", 3)
                .DeadLetterExchange(dlxName)
                .DeadLetterRoutingKey(dlqName));
        }
        else
        {
            configurator.DeclareQueue(srcQueue, durable: true, autoDelete: false, configure: q => q
                .DeadLetterExchange(dlxName)
                .DeadLetterRoutingKey(dlqName));
        }

        await adapter.DeployTopologyAsync(configurator.Build(), ct);
    }

    private static async Task PublishAsync(
        RabbitMqTransportAdapter adapter, string queueName, string messageId, CancellationToken ct)
    {
        OutboundMessage msg = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = string.Empty,
                [MsgIdHeader] = messageId,
            },
            body: Encoding.UTF8.GetBytes($"{{\"id\":\"{messageId}\"}}"),
            contentType: "application/json");

        await adapter.SendBatchAsync([msg], ct);
    }

    private async Task<IConnection> OpenRawConnectionAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory { Uri = new Uri(fixture.GetRabbitMqConnectionString()) };
        return await factory.CreateConnectionAsync(ct);
    }

    private async Task<int> GetMessageCountAsync(string queue, CancellationToken ct)
    {
        await using IConnection connection = await OpenRawConnectionAsync(ct);
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: ct);
        QueueDeclareOk ok = await channel.QueueDeclarePassiveAsync(queue, ct);
        return (int)ok.MessageCount;
    }

    private async Task<int> WaitForReadyCountAsync(string queue, int expected, TimeSpan timeout, CancellationToken ct)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        int count = await GetMessageCountAsync(queue, ct);
        while (count < expected && !timeoutCts.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
            count = await GetMessageCountAsync(queue, ct);
        }

        return count;
    }

    private async Task CleanUpAsync(string srcQueue, string dlqName, string dlxName)
    {
        // Best-effort cleanup on a fresh connection; never masks the test outcome.
        try
        {
            using CancellationTokenSource cleanupCts = new(TimeSpan.FromSeconds(15));
            await using IConnection connection = await OpenRawConnectionAsync(cleanupCts.Token);
            await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: cleanupCts.Token);
            await channel.QueueDeleteAsync(
                srcQueue,
                ifUnused: false,
                ifEmpty: false,
                cancellationToken: cleanupCts.Token);
            await channel.QueueDeleteAsync(
                dlqName,
                ifUnused: false,
                ifEmpty: false,
                cancellationToken: cleanupCts.Token);
            await channel.ExchangeDeleteAsync(dlxName, ifUnused: false, cancellationToken: cleanupCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TestContext.Current.SendDiagnosticMessage($"Cleanup of '{srcQueue}' failed: {ex.Message}");
        }
    }

    // ── Runner harness ────────────────────────────────────────────────────────

    private static ReceiveEndpointRunner BuildRunner(
        string queueName,
        string dlxName,
        string dlqName,
        ITransportAdapter adapter,
        BlockingTracker tracker)
    {
        EndpointBinding binding = new()
        {
            EndpointName = queueName,
            PrefetchCount = 32,
            ConcurrentMessageLimit = 1,
            RawConsumers = [typeof(BlockingRawConsumer)],
            DeadLetterExchange = dlxName,
            DeadLetterRoutingKey = dlqName,
        };

        var services = new ServiceCollection();
        services.AddSingleton(tracker);
        services.AddScoped<BlockingRawConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(Substitute.For<IMessageDeserializer>());

        return new ReceiveEndpointRunner(
            binding,
            adapter,
            deserializerResolver,
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<ISendEndpointProvider>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FlowController(NullLogger<FlowController>.Instance),
            new NullInstrumentation(),
            NullLogger<ReceiveEndpointRunner>.Instance);
    }

    // ── Shared types ──────────────────────────────────────────────────────────

    public enum QueueKind
    {
        Classic,
        Quorum,
    }

    private sealed record ShutdownMeasurement(
        QueueKind Kind, int RemainingInQueue, int DeadLettered, IReadOnlyList<int> DeliveryCounts)
    {
        public override string ToString() =>
            $"kind={Kind}, queue={RemainingInQueue}, dlq={DeadLettered}, " +
            $"x-delivery-count=[{string.Join(",", DeliveryCounts)}]";
    }

    private sealed class BlockingTracker
    {
        private readonly TaskCompletionSource _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ConcurrentBag<string> Received { get; } = [];

        internal Task FirstMessageReceived => _first.Task;

        internal void Record(string id)
        {
            Received.Add(id);
            _first.TrySetResult();
        }
    }

    /// <summary>Records the first delivery and then blocks until the runner cancels it.</summary>
    private sealed class BlockingRawConsumer(BlockingTracker tracker) : IRawConsumer
    {
        public async Task ConsumeAsync(RawConsumeContext context)
        {
            string id = context.Headers.TryGetValue(MsgIdHeader, out string? h) ? h : context.MessageId.ToString();
            tracker.Record(id);
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }
    }
}
