using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Kafka;
using BareWire.Transport.Kafka.Internal;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.Transport;

// ── Test message records ───────────────────────────────────────────────────────

/// <summary>Represents a Kafka order used in E2E publish/consume tests.</summary>
public sealed record TestKafkaOrder(string OrderId, decimal Amount, string Currency);

/// <summary>
/// End-to-end integration tests for <see cref="KafkaTransportAdapter"/> covering the full
/// message flow through a real Kafka broker: topology deploy → publish → consume → settle.
///
/// Each test creates isolated topics using a unique <see cref="Guid"/> suffix to prevent
/// cross-test interference. All tests require a running Kafka instance provisioned via
/// <see cref="AspireFixture"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaE2ETests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private KafkaTransportAdapter CreateAdapter(string groupId, Action<KafkaTransportOptions>? configure = null)
    {
        var options = new KafkaTransportOptions
        {
            BootstrapServers = fixture.GetKafkaBootstrapServers(),
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        };
        configure?.Invoke(options);
        return new KafkaTransportAdapter(options, NullLogger<KafkaTransportAdapter>.Instance);
    }

    /// <summary>
    /// Deploys a Kafka topic with the specified partition count.
    /// </summary>
    private static async Task DeployTopicAsync(
        KafkaTransportAdapter adapter,
        string topicName,
        int partitions,
        CancellationToken ct)
    {
        var configurator = new KafkaTopologyConfigurator();
        configurator.DeclareQueue(
            topicName,
            durable: true,
            autoDelete: false,
            configure: q => q.Argument(KafkaTopologyArguments.Partitions, partitions));
        await adapter.DeployTopologyAsync(configurator.Build(), ct);
    }

    private static FlowControlOptions StandardFlow() =>
        new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

    private static byte[] SerializeToJson<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value);

    private static T DeserializeFromSequence<T>(ReadOnlySequence<byte> body)
    {
        if (body.IsSingleSegment)
        {
            return JsonSerializer.Deserialize<T>(body.FirstSpan)
                ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");
        }

        byte[] buffer = new byte[body.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in body)
        {
            segment.Span.CopyTo(buffer.AsSpan(offset));
            offset += segment.Length;
        }

        return JsonSerializer.Deserialize<T>(buffer)
            ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");
    }

    private static byte[] ReadSequenceToArray(ReadOnlySequence<byte> sequence)
    {
        if (sequence.IsSingleSegment)
        {
            return sequence.FirstSpan.ToArray();
        }

        byte[] result = new byte[sequence.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in sequence)
        {
            segment.Span.CopyTo(result.AsSpan(offset));
            offset += segment.Length;
        }

        return result;
    }

    /// <summary>
    /// Consumes exactly one message and settles it with <paramref name="action"/> while the
    /// consumer is still active (settlement happens inside the enumeration, before the enumerator
    /// is disposed). Returns the consumed message for assertions. Optional <paramref name="inspect"/>
    /// runs against the message before settlement.
    /// </summary>
    private static async Task<InboundMessage> ConsumeOneAndSettleAsync(
        KafkaTransportAdapter adapter,
        string topicName,
        SettlementAction action,
        CancellationToken ct,
        Action<InboundMessage>? inspect = null)
    {
        await foreach (InboundMessage msg in adapter.ConsumeAsync(topicName, StandardFlow(), ct))
        {
            inspect?.Invoke(msg);
            await adapter.SettleAsync(action, msg, ct);
            return msg;
        }

        throw new InvalidOperationException("Consume stream ended before any message arrived.");
    }

    // ── E2E-K1: Typed publish → consume → deserialize ─────────────────────────

    /// <summary>
    /// E2E-K1: Publishes a typed <see cref="TestKafkaOrder"/> serialised as JSON, consumes it,
    /// deserialises the body and asserts field equality. Also verifies that the content-type
    /// header set on the outbound message is propagated end-to-end.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task TypedPublishConsume_EndToEnd_MessageDelivered()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"e2e-typed-{suffix}";
        string groupId = $"grp-{suffix}";

        await using KafkaTransportAdapter adapter = CreateAdapter(groupId);
        await DeployTopicAsync(adapter, topicName, partitions: 1, cts.Token);

        var order = new TestKafkaOrder(
            OrderId: $"ORD-{suffix[..8].ToUpperInvariant()}",
            Amount: 149.99m,
            Currency: "USD");

        byte[] body = SerializeToJson(order);

        OutboundMessage outbound = new(
            routingKey: topicName,
            headers: new Dictionary<string, string>(),
            body: body,
            contentType: "application/json");

        // Act — publish then consume; AutoOffsetReset.Earliest guarantees the consumer
        // reads from the start of the log even if it joins after the publish. Settlement happens
        // inside ConsumeOneAndSettleAsync while the consumer is still active.
        IReadOnlyList<SendResult> sendResults = await adapter.SendBatchAsync([outbound], cts.Token);

        // Assert — broker confirmed the send
        sendResults.Should().HaveCount(1);
        sendResults[0].IsConfirmed.Should().BeTrue();

        TestKafkaOrder? roundTripped = null;
        InboundMessage received = await ConsumeOneAndSettleAsync(
            adapter, topicName, SettlementAction.Ack, cts.Token,
            inspect: msg =>
            {
                // Assert — body deserialises to the original order
                roundTripped = DeserializeFromSequence<TestKafkaOrder>(msg.Body);

                // Assert — BW-Topic delivery header is stamped by the consumer
                msg.Headers.Should().ContainKey("BW-Topic");
            });

        received.Should().NotBeNull();
        roundTripped.Should().NotBeNull();
        roundTripped!.OrderId.Should().Be(order.OrderId);
        roundTripped.Amount.Should().Be(order.Amount);
        roundTripped.Currency.Should().Be(order.Currency);
    }

    // ── E2E-K2: Partition ordering ────────────────────────────────────────────

    /// <summary>
    /// E2E-K2: Publishes N=10 messages to a 3-partition topic, all carrying the SAME
    /// <c>BW-PartitionKey</c> value. Because all messages share a key they are routed to the
    /// same partition and must be consumed in the same strictly ascending order.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task PartitionOrdering_SameKey_PreservesOrder()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        const int TotalMessages = 10;
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"e2e-order-{suffix}";
        string groupId = $"grp-{suffix}";

        await using KafkaTransportAdapter adapter = CreateAdapter(groupId);
        await DeployTopicAsync(adapter, topicName, partitions: 3, cts.Token);

        // All messages carry the same partition key → same partition → preserved order.
        OutboundMessage[] messages = Enumerable
            .Range(1, TotalMessages)
            .Select(i => new OutboundMessage(
                routingKey: topicName,
                headers: new Dictionary<string, string>
                {
                    ["BW-PartitionKey"] = "order-42",
                },
                body: Encoding.UTF8.GetBytes($"{{\"seq\":{i}}}"),
                contentType: "application/json"))
            .ToArray();

        // Act — publish all messages, then consume them
        await adapter.SendBatchAsync(messages, cts.Token);

        var receivedSeqs = new List<int>(TotalMessages);
        FlowControlOptions flow = StandardFlow();

        await foreach (InboundMessage msg in adapter.ConsumeAsync(topicName, flow, cts.Token))
        {
            byte[] bodyBytes = ReadSequenceToArray(msg.Body);
            using JsonDocument doc = JsonDocument.Parse(bodyBytes);
            int seq = doc.RootElement.GetProperty("seq").GetInt32();
            receivedSeqs.Add(seq);

            await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);

            if (receivedSeqs.Count == TotalMessages)
            {
                break;
            }
        }

        // Assert — all messages received in strictly ascending order
        receivedSeqs.Should().HaveCount(TotalMessages);
        receivedSeqs.Should().BeInAscendingOrder(
            because: "messages with the same BW-PartitionKey go to the same partition and must arrive in publish order");
    }

    // ── E2E-K3: Idempotent producer — no duplicates ───────────────────────────

    /// <summary>
    /// E2E-K3: Publishes a batch of N distinct messages with idempotent producer enabled
    /// (<see cref="KafkaTransportOptions.EnableIdempotence"/> = true, <see cref="Acks.All"/>).
    /// After consuming, asserts every message is confirmed, the count is exactly N, and no
    /// duplicate bodies are present.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task IdempotentProducer_BatchPublish_NoDuplicates()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        const int TotalMessages = 8;
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"e2e-idem-{suffix}";
        string groupId = $"grp-{suffix}";

        // CreateAdapter defaults to EnableIdempotence=true and Acks=All — explicitly noted here.
        await using KafkaTransportAdapter adapter = CreateAdapter(groupId, opts =>
        {
            opts.EnableIdempotence = true;
            opts.Acks = Acks.All;
        });
        await DeployTopicAsync(adapter, topicName, partitions: 1, cts.Token);

        OutboundMessage[] messages = Enumerable
            .Range(1, TotalMessages)
            .Select(i => new OutboundMessage(
                routingKey: topicName,
                headers: new Dictionary<string, string>(),
                body: Encoding.UTF8.GetBytes($"{{\"id\":{i}}}"),
                contentType: "application/json"))
            .ToArray();

        // Act — publish the full batch with idempotent producer
        IReadOnlyList<SendResult> sendResults = await adapter.SendBatchAsync(messages, cts.Token);

        // Assert — all sends confirmed
        sendResults.Should().HaveCount(TotalMessages);
        sendResults.Should().AllSatisfy(r => r.IsConfirmed.Should().BeTrue());

        // Consume all messages and collect bodies
        var receivedBodies = new List<string>(TotalMessages);
        FlowControlOptions flow = StandardFlow();

        await foreach (InboundMessage msg in adapter.ConsumeAsync(topicName, flow, cts.Token))
        {
            receivedBodies.Add(Encoding.UTF8.GetString(ReadSequenceToArray(msg.Body)));
            await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);

            if (receivedBodies.Count == TotalMessages)
            {
                break;
            }
        }

        // Assert — exactly N messages, no duplicates
        receivedBodies.Should().HaveCount(TotalMessages,
            because: "idempotent producer must not introduce extra copies");
        receivedBodies.Should().OnlyHaveUniqueItems(
            because: "each message body must appear exactly once");
    }

    // ── E2E-K4: Retry topic on Defer ──────────────────────────────────────────

    /// <summary>
    /// E2E-K4: Enables the retry/DLQ pattern with a short base delay. Publishes a message,
    /// consumes it from the source topic, settles with <see cref="SettlementAction.Defer"/>,
    /// then consumes from the <c>.retry</c> topic and asserts the message arrived with the
    /// <c>BW-RetryCount</c> header present.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task RetryTopic_OnDefer_RepublishesToRetryTopic()
    {
        // 60 s: generous enough to cover the 50 ms base-delay republication + consumer join.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string sourceTopic = $"e2e-retry-{suffix}";
        string retryTopic = $"{sourceTopic}.retry";
        string dlqTopic = $"{sourceTopic}.DLQ";
        string groupId = $"grp-{suffix}";

        await using KafkaTransportAdapter adapter = CreateAdapter(groupId, opts =>
        {
            opts.RetryDlq.Enabled = true;
            opts.RetryDlq.MaxRetryCount = 3;
            opts.RetryDlq.BaseDelay = TimeSpan.FromMilliseconds(50);
            opts.RetryDlq.BackoffMultiplier = 2.0;
            opts.RetryDlq.MaxDelay = TimeSpan.FromSeconds(1);
        });

        // Deploy all three topics so the retry/DLQ producer can publish without errors.
        var configurator = new KafkaTopologyConfigurator();
        configurator.DeclareQueue(
            sourceTopic, durable: true, autoDelete: false,
            configure: q => q.Argument(KafkaTopologyArguments.Partitions, 1));
        configurator.DeclareQueue(
            retryTopic, durable: true, autoDelete: false,
            configure: q => q.Argument(KafkaTopologyArguments.Partitions, 1));
        configurator.DeclareQueue(
            dlqTopic, durable: true, autoDelete: false,
            configure: q => q.Argument(KafkaTopologyArguments.Partitions, 1));
        await adapter.DeployTopologyAsync(configurator.Build(), cts.Token);

        // Publish one message to the source topic
        OutboundMessage outbound = new(
            routingKey: sourceTopic,
            headers: new Dictionary<string, string>(),
            body: Encoding.UTF8.GetBytes("{\"step\":\"source\"}"),
            contentType: "application/json");
        await adapter.SendBatchAsync([outbound], cts.Token);

        // Consume from the source topic and Defer (settled inside the enumeration) → triggers
        // republication to the retry topic.
        await ConsumeOneAndSettleAsync(adapter, sourceTopic, SettlementAction.Defer, cts.Token);

        // Consume from the retry topic — allow for the BaseDelay before arrival — and Ack it inside
        // the enumeration. Assert the BW-RetryCount header is present before settlement.
        InboundMessage retryMsg = await ConsumeOneAndSettleAsync(
            adapter, retryTopic, SettlementAction.Ack, cts.Token,
            inspect: msg => msg.Headers.Should().ContainKey("BW-RetryCount",
                because: "Defer republishes with an incremented retry count header"));

        retryMsg.Should().NotBeNull();
    }

    // ── E2E-K5: DLQ on Reject ────────────────────────────────────────────────

    /// <summary>
    /// E2E-K5: Enables the retry/DLQ pattern. Publishes a message, consumes it, settles with
    /// <see cref="SettlementAction.Reject"/>, then consumes from the <c>.DLQ</c> topic and
    /// asserts that <c>BW-DeadLettered == "true"</c> and
    /// <c>BW-DeadLetterReason == "rejected"</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Dlq_OnReject_RepublishesToDlqTopic()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string sourceTopic = $"e2e-dlq-{suffix}";
        string retryTopic = $"{sourceTopic}.retry";
        string dlqTopic = $"{sourceTopic}.DLQ";
        string groupId = $"grp-{suffix}";

        await using KafkaTransportAdapter adapter = CreateAdapter(groupId, opts =>
        {
            opts.RetryDlq.Enabled = true;
            opts.RetryDlq.MaxRetryCount = 3;
            opts.RetryDlq.BaseDelay = TimeSpan.FromMilliseconds(50);
            opts.RetryDlq.BackoffMultiplier = 2.0;
            opts.RetryDlq.MaxDelay = TimeSpan.FromSeconds(1);
        });

        // Deploy all three topics
        var configurator = new KafkaTopologyConfigurator();
        configurator.DeclareQueue(
            sourceTopic, durable: true, autoDelete: false,
            configure: q => q.Argument(KafkaTopologyArguments.Partitions, 1));
        configurator.DeclareQueue(
            retryTopic, durable: true, autoDelete: false,
            configure: q => q.Argument(KafkaTopologyArguments.Partitions, 1));
        configurator.DeclareQueue(
            dlqTopic, durable: true, autoDelete: false,
            configure: q => q.Argument(KafkaTopologyArguments.Partitions, 1));
        await adapter.DeployTopologyAsync(configurator.Build(), cts.Token);

        // Publish one message to the source topic
        OutboundMessage outbound = new(
            routingKey: sourceTopic,
            headers: new Dictionary<string, string>(),
            body: Encoding.UTF8.GetBytes("{\"step\":\"source\"}"),
            contentType: "application/json");
        await adapter.SendBatchAsync([outbound], cts.Token);

        // Consume and Reject (settled inside the enumeration) → triggers immediate republication
        // to the DLQ topic.
        await ConsumeOneAndSettleAsync(adapter, sourceTopic, SettlementAction.Reject, cts.Token);

        // Consume from the DLQ topic and Ack it inside the enumeration; assert the dead-letter
        // tracking headers before settlement.
        InboundMessage dlqMsg = await ConsumeOneAndSettleAsync(
            adapter, dlqTopic, SettlementAction.Ack, cts.Token,
            inspect: msg =>
            {
                msg.Headers.Should().ContainKey("BW-DeadLettered");
                msg.Headers["BW-DeadLettered"].Should().Be("true");

                msg.Headers.Should().ContainKey("BW-DeadLetterReason");
                msg.Headers["BW-DeadLetterReason"].Should().Be("rejected");
            });

        dlqMsg.Should().NotBeNull();
    }

    // ── E2E-K6: Consumer group rebalance ─────────────────────────────────────

    /// <summary>
    /// E2E-K6: Starts two consumer loops sharing the same <c>GroupId</c> on a 3-partition topic,
    /// publishes N=12 messages, and asserts that the combined count across both consumers is
    /// exactly N (each message delivered once within the consumer group). Models the structure of
    /// <c>MultipleConsumers_SingleEndpoint_RoundRobin</c> in <c>RabbitMqE2ETests</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task ConsumerGroupRebalance_TwoConsumers_AllMessagesDeliveredOnce()
    {
        // 60 s covers CooperativeSticky rebalance + publish + consume.
        using CancellationTokenSource outerCts = new(TimeSpan.FromSeconds(60));
        const int TotalMessages = 12;
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"e2e-rebal-{suffix}";

        // Both adapters share the same GroupId so Kafka treats them as one consumer group.
        string sharedGroupId = $"grp-{suffix}";

        await using KafkaTransportAdapter adapter1 = CreateAdapter(sharedGroupId);
        await using KafkaTransportAdapter adapter2 = CreateAdapter(sharedGroupId);

        // Deploy the topic once (idempotent — second adapter would also succeed)
        await DeployTopicAsync(adapter1, topicName, partitions: 3, outerCts.Token);

        // Shared state: each consumer appends to its own bag; total across both must equal N.
        var consumer1Messages = new ConcurrentBag<InboundMessage>();
        var consumer2Messages = new ConcurrentBag<InboundMessage>();

        using CancellationTokenSource stopCts =
            CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token);
        int totalReceived = 0;

        async Task RunConsumerAsync(
            KafkaTransportAdapter adapter,
            ConcurrentBag<InboundMessage> bag,
            CancellationToken token)
        {
            // Low in-flight count encourages the group coordinator to distribute partitions
            // across both consumers after the first rebalance.
            FlowControlOptions flow = new() { MaxInFlightMessages = 2, InternalQueueCapacity = 20 };

            await foreach (InboundMessage msg in adapter.ConsumeAsync(topicName, flow, token))
            {
                bag.Add(msg);
                await adapter.SettleAsync(SettlementAction.Ack, msg, token);

                if (Interlocked.Increment(ref totalReceived) >= TotalMessages)
                {
                    await stopCts.CancelAsync();
                    break;
                }
            }
        }

        // Start both consumer loops, then wait ~1 s for partition assignment before publishing.
        // With CooperativeSticky the first rebalance is fast; 1 s is generous.
        Task consumer1Task = RunConsumerAsync(adapter1, consumer1Messages, stopCts.Token);
        Task consumer2Task = RunConsumerAsync(adapter2, consumer2Messages, stopCts.Token);

        await Task.Delay(TimeSpan.FromSeconds(1), outerCts.Token);

        // Publish N messages — spread across all 3 partitions via round-robin (no partition key)
        OutboundMessage[] messages = Enumerable
            .Range(1, TotalMessages)
            .Select(i => new OutboundMessage(
                routingKey: topicName,
                headers: new Dictionary<string, string>
                {
                    ["X-Seq"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                body: Encoding.UTF8.GetBytes($"{{\"seq\":{i}}}"),
                contentType: "application/json"))
            .ToArray();

        await adapter1.SendBatchAsync(messages, outerCts.Token);

        // Wait for both consumer loops to finish (via CancellationToken or natural exit)
        await Task.WhenAll(
            consumer1Task.ContinueWith(_ => Task.CompletedTask, TaskContinuationOptions.None),
            consumer2Task.ContinueWith(_ => Task.CompletedTask, TaskContinuationOptions.None));

        // Assert — combined count is exactly N (each message delivered once in the group)
        int combined = consumer1Messages.Count + consumer2Messages.Count;
        combined.Should().Be(TotalMessages,
            because: "all published messages must be delivered exactly once across the consumer group");

        // Assert — no duplicate delivery within each individual consumer's bag
        consumer1Messages.Select(m => m.DeliveryTag).Should()
            .OnlyHaveUniqueItems(because: "consumer 1 must not receive the same message twice");
        consumer2Messages.Select(m => m.DeliveryTag).Should()
            .OnlyHaveUniqueItems(because: "consumer 2 must not receive the same message twice");
    }
}
