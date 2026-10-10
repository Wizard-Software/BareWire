using System.Collections.Concurrent;
using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Topology;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Measures what a real Azure Service Bus endpoint (emulator) does with the messages a consumer holds when its
/// <see cref="ReceiveEndpointRunner"/> is cancelled while a handler is blocked. Five messages are sent to a
/// queue with a 60 s lock duration, the handler blocks on its cancellation token, the runner token is
/// cancelled once the receive buffer has filled, and the adapter is disposed. A raw receiver then reads the
/// queue for up to 15 s: the messages come back within that window only if the adapter explicitly released
/// them. Each test uses a unique queue name.
/// </summary>
[Trait("Category", "AzureServiceBus")]
public sealed class AzureServiceBusShutdownRequeueTests
{
    private const int MessageCount = 5;
    private const string MsgIdHeader = "test-msg-id";
    private const string SessionId = "shutdown-session";
    private static readonly TimeSpan _readWindow = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task RunAsync_ConsumerBlockedOnCancellation_RequeuesEachMessageAtMostOnce()
    {
        AzureServiceBusTestEnvironment.SkipIfUnavailable();

        ShutdownResult result = await RunShutdownScenarioAsync(useSessions: false, TestContext.Current.CancellationToken);

        AssertShutdownOutcome(result);
    }

    [Fact]
    public async Task RunAsync_SessionConsumerBlockedOnCancellation_RequeuesEachMessageAtMostOnce()
    {
        AzureServiceBusTestEnvironment.SkipIfUnavailable();

        ShutdownResult result = await RunShutdownScenarioAsync(useSessions: true, TestContext.Current.CancellationToken);

        AssertShutdownOutcome(result);
    }

    private static void AssertShutdownOutcome(ShutdownResult result)
    {
        // Every message the adapter merely buffered must come back inside the read window. The message the
        // blocked handler holds is abandoned by the runner at the moment the consumer stops; it is asserted
        // below as "at most once" and "never delivered to the stopped consumer again" instead.
        string[] buffered = Enumerable.Range(0, MessageCount)
            .Select(i => $"msg-{i}")
            .Where(id => id != result.InFlightId)
            .ToArray();
        result.ReceivedAfterStop.Should().Contain(
            buffered, "every buffered message must be released, not held until its lock expires ({0})", result);
        result.DeliveriesToStoppedConsumer.Should().Be(
            1, "the requeued in-flight message must not return to the stopping consumer ({0})", result);
        result.DeliveryCounts.Should().NotBeEmpty("({0})", result);
        result.DeliveryCounts.Max().Should().BeLessThanOrEqualTo(
            2, "each message is redelivered at most once per shutdown ({0})", result);
    }

    // ── Scenario ──────────────────────────────────────────────────────────────

    private static async Task<ShutdownResult> RunShutdownScenarioAsync(bool useSessions, CancellationToken testToken)
    {
        string queueName = $"test-shutdown-{Guid.NewGuid():N}";

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        CancellationToken ct = cts.Token;

        await using ServiceBusClient raw = new(AzureServiceBusTestEnvironment.ConnectionString!);

        try
        {
            await using (AzureServiceBusTransportAdapter provisioner = AzureServiceBusTestEnvironment.CreateSasAdapter())
            {
                await provisioner.DeployTopologyAsync(
                    new TopologyDeclaration
                    {
                        Queues =
                        [
                            new QueueDeclaration(
                                Name: queueName,
                                Durable: true,
                                Arguments: new Dictionary<string, object>
                                {
                                    [AzureServiceBusTopologyArguments.LockDuration] = TimeSpan.FromSeconds(60),
                                    [AzureServiceBusTopologyArguments.MaxDeliveryCount] = 10,
                                    [AzureServiceBusTopologyArguments.RequiresSession] = useSessions,
                                }),
                        ],
                    },
                    ct);
            }

            await using (ServiceBusSender sender = raw.CreateSender(queueName))
            {
                for (int i = 0; i < MessageCount; i++)
                {
                    var message = new ServiceBusMessage(BinaryData.FromString($"{{\"id\":\"msg-{i}\"}}"))
                    {
                        MessageId = $"msg-{i}",
                        ContentType = "application/json",
                    };
                    message.ApplicationProperties[MsgIdHeader] = $"msg-{i}";
                    if (useSessions)
                    {
                        message.SessionId = SessionId;
                    }

                    await sender.SendMessageAsync(message, ct);
                }
            }

            var tracker = new BlockingTracker();

            await using (AzureServiceBusTransportAdapter adapter = useSessions
                ? AzureServiceBusTestEnvironment.CreateSasAdapter(c => c.UseSessions())
                : AzureServiceBusTestEnvironment.CreateSasAdapter())
            {
                ReceiveEndpointRunner runner = BuildRunner(queueName, adapter, tracker);

                using CancellationTokenSource runnerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task runTask = runner.RunAsync(runnerCts.Token);

                await tracker.FirstMessageReceived.WaitAsync(ct);

                // Let the receive loop pull the remaining messages into the receive buffer.
                await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);

                await runnerCts.CancelAsync();
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    // Expected: cooperative shutdown surfaces as cancellation.
                }
            }

            ShutdownResult measurement = await MeasureAsync(raw, queueName, useSessions, tracker, ct);
            TestContext.Current.SendDiagnosticMessage("Shutdown measurement: " + measurement);
            return measurement;
        }
        finally
        {
            await AzureServiceBusTestEnvironment.TryDeleteQueueAsync(queueName, CancellationToken.None);
        }
    }

    private static async Task<ShutdownResult> MeasureAsync(
        ServiceBusClient raw, string queueName, bool useSessions, BlockingTracker tracker, CancellationToken ct)
    {
        var deliveryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();

        ServiceBusReceiver? receiver = null;
        try
        {
            while (deliveryCounts.Count < MessageCount && stopwatch.Elapsed < _readWindow)
            {
                if (receiver is null)
                {
                    receiver = useSessions
                        ? await TryAcceptSessionAsync(raw, queueName, ct)
                        : raw.CreateReceiver(queueName, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });

                    if (receiver is null)
                    {
                        // The session is still locked by the stopped consumer; keep trying within the window.
                        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                        continue;
                    }
                }

                IReadOnlyList<ServiceBusReceivedMessage> batch = await receiver.ReceiveMessagesAsync(
                    maxMessages: 10, maxWaitTime: TimeSpan.FromSeconds(1), cancellationToken: ct);

                foreach (ServiceBusReceivedMessage message in batch)
                {
                    string id = (string)message.ApplicationProperties[MsgIdHeader];
                    deliveryCounts[id] = message.DeliveryCount;
                }
            }
        }
        finally
        {
            if (receiver is not null)
            {
                await receiver.DisposeAsync();
            }
        }

        return new ShutdownResult(
            deliveryCounts.Keys.Order(StringComparer.Ordinal).ToList(),
            deliveryCounts.Values.ToList(),
            tracker.Received.Count,
            tracker.FirstId,
            stopwatch.Elapsed);
    }

    private static async Task<ServiceBusReceiver?> TryAcceptSessionAsync(
        ServiceBusClient raw, string queueName, CancellationToken ct)
    {
        try
        {
            return await raw.AcceptSessionAsync(
                queueName,
                SessionId,
                new ServiceBusSessionReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock },
                ct);
        }
        catch (ServiceBusException ex) when (ex.Reason is ServiceBusFailureReason.SessionCannotBeLocked
                                                or ServiceBusFailureReason.ServiceTimeout)
        {
            return null;
        }
    }

    // ── Runner harness ────────────────────────────────────────────────────────

    private static ReceiveEndpointRunner BuildRunner(
        string queueName, ITransportAdapter adapter, BlockingTracker tracker)
    {
        EndpointBinding binding = new()
        {
            EndpointName = queueName,
            PrefetchCount = 32,
            ConcurrentMessageLimit = 1,
            RawConsumers = [typeof(BlockingRawConsumer)],
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

    /// <summary>
    /// Outcome of one scenario run. <see cref="ToString"/> prints counts and test message ids only, never
    /// message bodies, lock tokens or connection details.
    /// </summary>
    private sealed record ShutdownResult(
        IReadOnlyList<string> ReceivedAfterStop,
        IReadOnlyList<int> DeliveryCounts,
        int DeliveriesToStoppedConsumer,
        string? InFlightId,
        TimeSpan Elapsed)
    {
        public override string ToString() =>
            $"receivedAfterStop={ReceivedAfterStop.Count} [{string.Join(",", ReceivedAfterStop)}], " +
            $"deliveryCounts=[{string.Join(",", DeliveryCounts)}], deliveriesToStoppedConsumer={DeliveriesToStoppedConsumer}, " +
            $"inFlight={InFlightId}, readElapsedMs={(long)Elapsed.TotalMilliseconds}";
    }

    private sealed class BlockingTracker
    {
        private readonly TaskCompletionSource _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ConcurrentBag<string> Received { get; } = [];

        internal Task FirstMessageReceived => _first.Task;

        internal string? FirstId { get; private set; }

        internal void Record(string id)
        {
            Received.Add(id);
            FirstId ??= id;
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
