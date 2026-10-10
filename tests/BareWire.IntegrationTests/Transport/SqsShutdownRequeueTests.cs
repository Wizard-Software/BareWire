using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire.Transport.AWS.SQS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Measures what a real SQS endpoint (LocalStack) does with the messages a consumer holds when its
/// <see cref="ReceiveEndpointRunner"/> is cancelled while a handler is blocked. Five messages are sent to a
/// queue with a 60 s visibility timeout, the handler blocks on its cancellation token, the runner token is
/// cancelled once the receive buffer has filled, and the adapter is disposed. A raw SQS client then reads the
/// queue for up to 15 s: the messages come back within that window only if the adapter explicitly made them
/// visible again. Each test uses a unique queue name.
/// </summary>
[Trait("Category", "AwsSqs")]
public sealed class SqsShutdownRequeueTests
{
    private const int MessageCount = 5;
    private const string MsgIdHeader = "test-msg-id";
    private static readonly TimeSpan _readWindow = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task RunAsync_ConsumerBlockedOnCancellation_RequeuesEachMessageAtMostOnce()
    {
        SqsTestEnvironment.SkipIfUnavailable();

        ShutdownResult result = await RunShutdownScenarioAsync(TestContext.Current.CancellationToken);

        // The message the blocked handler holds is requeued by the runner at the very moment the consumer stops.
        // A long poll that is still in flight at that instant can receive it on the broker and lose the response
        // to the cancellation, which leaves it invisible until the visibility timeout (inherent to at-least-once
        // SQS delivery, not something the adapter can prevent). It is therefore the one message this test does
        // not require back; the messages the adapter itself buffered must always be handed back.
        string[] buffered = Enumerable.Range(0, MessageCount)
            .Select(i => $"msg-{i}")
            .Where(id => id != result.InFlightId)
            .ToArray();
        result.ReceivedAfterStop.Should().Contain(
            buffered, "every buffered message must be made visible again, not held until its visibility timeout ({0})", result);
        result.DeliveriesToStoppedConsumer.Should().Be(
            1, "the requeued in-flight message must not return to the stopping consumer ({0})", result);
        result.ReceiveCounts.Should().NotBeEmpty("({0})", result);
        result.ReceiveCounts.Max().Should().BeLessThanOrEqualTo(
            2, "each message is redelivered at most once per shutdown ({0})", result);
    }

    // ── Scenario ──────────────────────────────────────────────────────────────

    private static async Task<ShutdownResult> RunShutdownScenarioAsync(CancellationToken testToken)
    {
        string queueName = $"test-shutdown-{Guid.NewGuid():N}";

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        CancellationToken ct = cts.Token;

        using AmazonSQSClient raw = CreateRawClient();

        try
        {
            CreateQueueResponse created = await raw.CreateQueueAsync(
                new CreateQueueRequest
                {
                    QueueName = queueName,
                    Attributes = new Dictionary<string, string> { ["VisibilityTimeout"] = "60" },
                },
                ct);

            for (int i = 0; i < MessageCount; i++)
            {
                await SendAsync(raw, created.QueueUrl, $"msg-{i}", ct);
            }

            var tracker = new BlockingTracker();

            await using (SqsTransportAdapter adapter = SqsTestEnvironment.CreateAdapter())
            {
                ReceiveEndpointRunner runner = BuildRunner(queueName, adapter, tracker);

                using CancellationTokenSource runnerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task runTask = runner.RunAsync(runnerCts.Token);

                await tracker.FirstMessageReceived.WaitAsync(ct);

                // Let the long-polling loop pull the remaining messages into the receive buffer.
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);

                await runnerCts.CancelAsync();
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    // Expected: cooperative shutdown surfaces as cancellation.
                }
            }

            ShutdownResult measurement = await MeasureAsync(raw, created.QueueUrl, tracker, ct);
            TestContext.Current.SendDiagnosticMessage("Shutdown measurement: " + measurement);
            return measurement;
        }
        finally
        {
            await SqsTestEnvironment.TryDeleteQueueAsync(queueName, CancellationToken.None);
        }
    }

    private static async Task<ShutdownResult> MeasureAsync(
        AmazonSQSClient raw, string queueUrl, BlockingTracker tracker, CancellationToken ct)
    {
        var receiveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();

        while (receiveCounts.Count < MessageCount && stopwatch.Elapsed < _readWindow)
        {
            ReceiveMessageResponse response = await raw.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 1,
                    MessageAttributeNames = ["All"],
                    MessageSystemAttributeNames = ["ApproximateReceiveCount"],
                },
                ct);

            // AWSSDK.SQS v4 returns null (not an empty list) when nothing was received.
            foreach (Message message in response.Messages ?? [])
            {
                string id = message.MessageAttributes[MsgIdHeader].StringValue;
                int count = int.Parse(
                    message.Attributes["ApproximateReceiveCount"], NumberStyles.Integer, CultureInfo.InvariantCulture);
                receiveCounts[id] = count;
            }
        }

        return new ShutdownResult(
            receiveCounts.Keys.Order(StringComparer.Ordinal).ToList(),
            receiveCounts.Values.ToList(),
            tracker.Received.Count,
            tracker.FirstId);
    }

    // ── Broker helpers ────────────────────────────────────────────────────────

    private static AmazonSQSClient CreateRawClient() =>
        new(
            new BasicAWSCredentials("test", "test"),
            new AmazonSQSConfig
            {
                ServiceURL = SqsTestEnvironment.ServiceUrl,
                AuthenticationRegion = "us-east-1",
            });

    private static async Task SendAsync(AmazonSQSClient raw, string queueUrl, string messageId, CancellationToken ct)
    {
        await raw.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = $"{{\"id\":\"{messageId}\"}}",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["content-type"] = new() { DataType = "String", StringValue = "application/json" },
                    [MsgIdHeader] = new() { DataType = "String", StringValue = messageId },
                },
            },
            ct);
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
    /// message bodies or SQS identifiers.
    /// </summary>
    private sealed record ShutdownResult(
        IReadOnlyList<string> ReceivedAfterStop,
        IReadOnlyList<int> ReceiveCounts,
        int DeliveriesToStoppedConsumer,
        string? InFlightId)
    {
        public override string ToString() =>
            $"receivedAfterStop={ReceivedAfterStop.Count} [{string.Join(",", ReceivedAfterStop)}], " +
            $"receiveCounts=[{string.Join(",", ReceiveCounts)}], deliveriesToStoppedConsumer={DeliveriesToStoppedConsumer}, inFlight={InFlightId}";
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
