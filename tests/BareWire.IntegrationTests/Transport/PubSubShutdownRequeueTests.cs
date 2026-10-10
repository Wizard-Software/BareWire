using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire.Transport.Google.PubSub;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Measures what a real Pub/Sub endpoint (emulator) does with the messages a consumer holds when its
/// <see cref="ReceiveEndpointRunner"/> is cancelled while a handler is blocked. Five messages are published to
/// a topic whose subscription has a 60 s ack deadline, the handler blocks on its cancellation token, the runner
/// token is cancelled once the receive buffer has filled, and the adapter is disposed. A raw subscriber client
/// then pulls for up to 15 s: the messages come back within that window only if the adapter explicitly made
/// them deliverable again. Each test uses a unique topic and subscription name.
/// </summary>
[Trait("Category", "GooglePubSub")]
public sealed class PubSubShutdownRequeueTests
{
    private const string ProjectId = "barewire-test";
    private const int MessageCount = 5;
    private const string MsgIdHeader = "test-msg-id";
    private static readonly TimeSpan _readWindow = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task RunAsync_ConsumerBlockedOnCancellation_RequeuesEachMessageAtMostOnce()
    {
        PubSubTestEnvironment.SkipIfUnavailable();

        ShutdownResult result = await RunShutdownScenarioAsync(TestContext.Current.CancellationToken);

        // The message the blocked handler holds is requeued by the runner at the very moment the consumer
        // stops. A pull that is still in flight at that instant can receive it and lose the response to the
        // cancellation (the adapter hands such a response straight back, but the race is inherent to
        // at-least-once delivery), so it is the one message this test does not require back; every message
        // the adapter itself buffered must always be made deliverable again.
        string[] buffered = Enumerable.Range(0, MessageCount)
            .Select(i => $"msg-{i}")
            .Where(id => id != result.InFlightId)
            .ToArray();
        result.ReceivedAfterStop.Should().Contain(
            buffered, "every buffered message must be made deliverable again, not held until its ack deadline ({0})", result);
        result.DeliveriesToStoppedConsumer.Should().Be(
            1, "the requeued in-flight message must not return to the stopping consumer ({0})", result);
        result.DeliveryCounts.Should().NotBeEmpty("({0})", result);
        result.DeliveryCounts.Max().Should().BeLessThanOrEqualTo(
            2, "each message is redelivered at most once per shutdown ({0})", result);
    }

    // ── Scenario ──────────────────────────────────────────────────────────────

    private static async Task<ShutdownResult> RunShutdownScenarioAsync(CancellationToken testToken)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"test-shutdown-topic-{suffix}";
        string subName = $"test-shutdown-sub-{suffix}";

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        CancellationToken ct = cts.Token;

        PublisherServiceApiClient publisher = new PublisherServiceApiClientBuilder
        {
            Endpoint = PubSubTestEnvironment.EmulatorHost,
            ChannelCredentials = ChannelCredentials.Insecure,
        }.Build();
        SubscriberServiceApiClient raw = new SubscriberServiceApiClientBuilder
        {
            Endpoint = PubSubTestEnvironment.EmulatorHost,
            ChannelCredentials = ChannelCredentials.Insecure,
        }.Build();

        var topic = TopicName.FromProjectTopic(ProjectId, topicName);
        var subscription = SubscriptionName.FromProjectSubscription(ProjectId, subName);

        try
        {
            await publisher.CreateTopicAsync(topic, ct);
            await raw.CreateSubscriptionAsync(
                new Subscription { SubscriptionName = subscription, TopicAsTopicName = topic, AckDeadlineSeconds = 60 },
                ct);

            for (int i = 0; i < MessageCount; i++)
            {
                await PublishAsync(publisher, topic, $"msg-{i}", ct);
            }

            var tracker = new BlockingTracker();
            TimeSpan stopTime;

            await using (PubSubTransportAdapter adapter = PubSubTestEnvironment.CreateAdapter(ProjectId))
            {
                ReceiveEndpointRunner runner = BuildRunner(subName, adapter, tracker);

                using CancellationTokenSource runnerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task runTask = runner.RunAsync(runnerCts.Token);

                await tracker.FirstMessageReceived.WaitAsync(ct);

                // Let the polling loop pull the remaining messages into the receive buffer.
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);

                var stopwatch = Stopwatch.StartNew();
                await runnerCts.CancelAsync();
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    // Expected: cooperative shutdown surfaces as cancellation.
                }

                stopTime = stopwatch.Elapsed;
            }

            ShutdownResult measurement = await MeasureAsync(raw, subscription, tracker, stopTime, ct);
            TestContext.Current.SendDiagnosticMessage("Shutdown measurement: " + measurement);
            return measurement;
        }
        finally
        {
            await PubSubTestEnvironment.TryDeleteSubscriptionAsync(ProjectId, subName, CancellationToken.None);
            await PubSubTestEnvironment.TryDeleteTopicAsync(ProjectId, topicName, CancellationToken.None);
        }
    }

    private static async Task<ShutdownResult> MeasureAsync(
        SubscriberServiceApiClient raw,
        SubscriptionName subscription,
        BlockingTracker tracker,
        TimeSpan stopTime,
        CancellationToken ct)
    {
        var rawCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();

        while (rawCounts.Count < MessageCount && stopwatch.Elapsed < _readWindow)
        {
            // The emulator holds a pull open until a message is available, so each call is bounded by what
            // is left of the read window; otherwise a message that only returns after its ack deadline would
            // still be counted.
            using CancellationTokenSource pullCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pullCts.CancelAfter(_readWindow - stopwatch.Elapsed);

            PullResponse response;
            try
            {
                response = await raw.PullAsync(subscription, maxMessages: 10, pullCts.Token);
            }
            catch (Exception ex) when (
                !ct.IsCancellationRequested &&
                (ex is OperationCanceledException ||
                 ex is RpcException { StatusCode: StatusCode.Cancelled or StatusCode.DeadlineExceeded }))
            {
                break; // read window elapsed
            }

            foreach (ReceivedMessage received in response.ReceivedMessages)
            {
                string id = received.Message.Attributes[MsgIdHeader];
                rawCounts[id] = rawCounts.GetValueOrDefault(id) + 1;
            }

            if (response.ReceivedMessages.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            }
        }

        // A delivery is one hand-out of a message by the broker: to the stopped consumer or to the raw client.
        List<int> deliveryCounts = rawCounts
            .Select(kv => kv.Value + tracker.Received.Count(r => r == kv.Key))
            .ToList();

        return new ShutdownResult(
            rawCounts.Keys.Order(StringComparer.Ordinal).ToList(),
            deliveryCounts,
            tracker.Received.Count,
            tracker.FirstId,
            stopTime,
            stopwatch.Elapsed);
    }

    // ── Broker helpers ────────────────────────────────────────────────────────

    private static async Task PublishAsync(
        PublisherServiceApiClient publisher, TopicName topic, string messageId, CancellationToken ct)
    {
        var message = new PubsubMessage { Data = ByteString.CopyFromUtf8($"{{\"id\":\"{messageId}\"}}") };
        message.Attributes["content-type"] = "application/json";
        message.Attributes[MsgIdHeader] = messageId;
        await publisher.PublishAsync(topic, [message], ct);
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
    /// Outcome of one scenario run. <see cref="ToString"/> prints counts, timings and test message ids only,
    /// never message bodies or Pub/Sub ack ids.
    /// </summary>
    private sealed record ShutdownResult(
        IReadOnlyList<string> ReceivedAfterStop,
        IReadOnlyList<int> DeliveryCounts,
        int DeliveriesToStoppedConsumer,
        string? InFlightId,
        TimeSpan StopTime,
        TimeSpan ReadTime)
    {
        public override string ToString() =>
            $"receivedAfterStop={ReceivedAfterStop.Count} [{string.Join(",", ReceivedAfterStop)}], " +
            $"deliveryCounts=[{string.Join(",", DeliveryCounts)}], deliveriesToStoppedConsumer={DeliveriesToStoppedConsumer}, " +
            $"inFlight={InFlightId}, stopTime={StopTime.TotalMilliseconds:F0} ms, readTime={ReadTime.TotalMilliseconds:F0} ms";
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
