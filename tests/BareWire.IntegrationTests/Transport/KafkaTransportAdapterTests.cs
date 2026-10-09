using System.Buffers;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Kafka;
using BareWire.Transport.Kafka.Internal;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Integration tests for publish, consume, and settlement operations via
/// <see cref="KafkaTransportAdapter"/>. All tests use a real Kafka broker provisioned
/// by <see cref="AspireFixture"/>. Each test creates an isolated topic with a unique
/// name suffix to prevent cross-test interference.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaTransportAdapterTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Factory & helpers ─────────────────────────────────────────────────────

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

    private static OutboundMessage MakeMessage(string topicName, string payload = "{\"ok\":true}") =>
        new(
            routingKey: topicName,
            headers: new Dictionary<string, string>(),
            body: Encoding.UTF8.GetBytes(payload),
            contentType: "application/json");

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
    /// Reads exactly one message from the adapter's consume stream, honouring the given timeout.
    /// </summary>
    private static async Task<InboundMessage> ConsumeOneAsync(
        KafkaTransportAdapter adapter,
        string topicName,
        CancellationToken ct)
    {
        FlowControlOptions flow = new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

        await foreach (InboundMessage msg in adapter.ConsumeAsync(topicName, flow, ct))
        {
            return msg;
        }

        throw new InvalidOperationException("Consume stream ended before a message was received.");
    }

    // ── SendBatchAsync — publisher confirms ───────────────────────────────────

    [Fact]
    public async Task SendBatchAsync_SingleMessage_IsConfirmed()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"t-single-{suffix}";
        string groupId = $"grp-{suffix}";

        await using KafkaTransportAdapter adapter = CreateAdapter(groupId);
        await DeployTopicAsync(adapter, topicName, partitions: 1, cts.Token);

        OutboundMessage message = MakeMessage(topicName);

        // Act
        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync([message], cts.Token);

        // Assert
        results.Should().HaveCount(1);
        results[0].IsConfirmed.Should().BeTrue();
    }

    [Fact]
    public async Task SendBatchAsync_MultipleMessages_AllConfirmed()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"t-batch-{suffix}";
        string groupId = $"grp-{suffix}";

        await using KafkaTransportAdapter adapter = CreateAdapter(groupId);
        await DeployTopicAsync(adapter, topicName, partitions: 1, cts.Token);

        OutboundMessage[] messages =
        [
            MakeMessage(topicName, "{\"seq\":1}"),
            MakeMessage(topicName, "{\"seq\":2}"),
            MakeMessage(topicName, "{\"seq\":3}"),
        ];

        // Act
        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(messages, cts.Token);

        // Assert — all three messages confirmed
        results.Should().HaveCount(3);
        results.Should().AllSatisfy(r => r.IsConfirmed.Should().BeTrue());
    }

    // ── ConsumeAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_AfterPublish_ReceivesMessage()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"t-consume-{suffix}";
        string groupId = $"grp-{suffix}";

        await using KafkaTransportAdapter adapter = CreateAdapter(groupId);
        await DeployTopicAsync(adapter, topicName, partitions: 1, cts.Token);

        byte[] expectedBody = Encoding.UTF8.GetBytes("{\"msg\":\"hello\"}");
        OutboundMessage outbound = new(
            routingKey: topicName,
            headers: new Dictionary<string, string> { ["x-test"] = "roundtrip" },
            body: expectedBody,
            contentType: "application/json");

        // Publish before opening the consume stream; AutoOffsetReset.Earliest guarantees the
        // consumer reads from the beginning of the log even if it joins after publish.
        await adapter.SendBatchAsync([outbound], cts.Token);

        // Act — consume and settle INSIDE the enumeration. Returning out of the await-foreach
        // disposes the enumerator, which stops + unregisters the consumer; SettleAsync resolves
        // the consumer from the registry by BW-ConsumerId, so it must be called while the
        // consumer is still active (before the loop is exited).
        FlowControlOptions flow = new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };
        InboundMessage? received = null;

        await foreach (InboundMessage msg in adapter.ConsumeAsync(topicName, flow, cts.Token))
        {
            received = msg;

            // Assert — body round-trips correctly
            ReadSequenceToArray(msg.Body).Should().BeEquivalentTo(expectedBody);

            // Assert — BareWire delivery headers are stamped by the consumer
            msg.Headers.Should().ContainKey("BW-Topic");
            msg.Headers.Should().ContainKey("BW-ConsumerId");

            // Assert — application header is propagated
            msg.Headers.Should().ContainKey("x-test");
            msg.Headers["x-test"].Should().Be("roundtrip");

            // Settle while the consumer is still active, then exit the loop.
            await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);
            break;
        }

        received.Should().NotBeNull("a message must have been consumed");
    }

    // ── SettleAsync — Defer (retry/DLQ disabled) ──────────────────────────────

    [Fact]
    public async Task SettleAsync_Defer_WhenRetryDlqDisabled_ThrowsNotSupported()
    {
        // Arrange — RetryDlq.Enabled defaults to false
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"t-defer-{suffix}";
        string groupId = $"grp-{suffix}";

        await using KafkaTransportAdapter adapter = CreateAdapter(groupId);
        await DeployTopicAsync(adapter, topicName, partitions: 1, cts.Token);

        await adapter.SendBatchAsync([MakeMessage(topicName)], cts.Token);

        InboundMessage received = await ConsumeOneAsync(adapter, topicName, cts.Token);

        // Act + Assert — Defer must throw NotSupportedException when retry/DLQ is disabled
        Func<Task> defer = async () =>
            await adapter.SettleAsync(SettlementAction.Defer, received, cts.Token);

        await defer.Should().ThrowAsync<NotSupportedException>();
    }

    // ── SettleAsync — commit position must not pass unsettled offsets (B17) ──

    /// <summary>
    /// Consumes one session from <paramref name="topicName"/>, settling the n-th delivered message
    /// with <c>actions[n]</c>, and returns the bodies in delivery order. Leaving the enumeration
    /// closes the consumer, which commits the stored offsets.
    /// </summary>
    private static async Task<List<string>> RunSessionAsync(
        KafkaTransportAdapter adapter,
        string topicName,
        SettlementAction[] actions,
        CancellationToken ct)
    {
        FlowControlOptions flow = new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };
        List<string> bodies = [];

        await foreach (InboundMessage msg in adapter.ConsumeAsync(topicName, flow, ct))
        {
            bodies.Add(Encoding.UTF8.GetString(ReadSequenceToArray(msg.Body)));
            await adapter.SettleAsync(actions[bodies.Count - 1], msg, ct);
            msg.Dispose();

            if (bodies.Count == actions.Length)
            {
                break;
            }
        }

        return bodies;
    }

    [Fact]
    public async Task SettleAsync_RequeueBetweenAcks_RedeliveryStartsAtRequeuedOffset()
    {
        // Arrange — one partition, three messages, one consumer group
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"t-requeue-{suffix}";
        string groupId = $"grp-{suffix}";

        await using (KafkaTransportAdapter first = CreateAdapter(groupId))
        {
            await DeployTopicAsync(first, topicName, partitions: 1, cts.Token);
            await first.SendBatchAsync(
                [MakeMessage(topicName, "{\"n\":1}"), MakeMessage(topicName, "{\"n\":2}"), MakeMessage(topicName, "{\"n\":3}")],
                cts.Token);

            // Act — ack offset 0, requeue offset 1, ack offset 2, then leave (Close() commits stored offsets)
            List<string> bodies = await RunSessionAsync(
                first,
                topicName,
                [SettlementAction.Ack, SettlementAction.Requeue, SettlementAction.Ack],
                cts.Token);
            bodies.Should().Equal("{\"n\":1}", "{\"n\":2}", "{\"n\":3}");
        }

        // Assert — a fresh consumer in the same group starts at the requeued offset 1 (not 0, not past 2)
        await using KafkaTransportAdapter second = CreateAdapter(groupId);
        using CancellationTokenSource redeliveryCts = new(TimeSpan.FromSeconds(20));
        InboundMessage redelivered = await ConsumeOneAsync(second, topicName, redeliveryCts.Token);
        Encoding.UTF8.GetString(ReadSequenceToArray(redelivered.Body)).Should().Be("{\"n\":2}");
    }

    [Fact]
    public async Task SettleAsync_NackWithoutRetry_HoldsCommitAcrossRestarts()
    {
        // Arrange — retry/DLQ disabled (default): Nack pins the partition commit position
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(90));
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"t-nack-{suffix}";
        string groupId = $"grp-{suffix}";

        await using (KafkaTransportAdapter first = CreateAdapter(groupId))
        {
            await DeployTopicAsync(first, topicName, partitions: 1, cts.Token);
            await first.SendBatchAsync(
                [MakeMessage(topicName, "{\"n\":1}"), MakeMessage(topicName, "{\"n\":2}")],
                cts.Token);

            // Act — session 1: nack offset 0, ack offset 1
            List<string> bodies = await RunSessionAsync(
                first, topicName, [SettlementAction.Nack, SettlementAction.Ack], cts.Token);
            bodies.Should().Equal("{\"n\":1}", "{\"n\":2}");
        }

        // Session 2 — the nacked message is redelivered first; nack it again, ack the next one
        await using (KafkaTransportAdapter second = CreateAdapter(groupId))
        {
            List<string> bodies = await RunSessionAsync(
                second, topicName, [SettlementAction.Nack, SettlementAction.Ack], cts.Token);
            bodies.Should().StartWith("{\"n\":1}");
        }

        // Session 3 — still pinned at offset 0 (accepted behaviour without retry/DLQ)
        await using KafkaTransportAdapter third = CreateAdapter(groupId);
        using CancellationTokenSource redeliveryCts = new(TimeSpan.FromSeconds(20));
        InboundMessage redelivered = await ConsumeOneAsync(third, topicName, redeliveryCts.Token);
        Encoding.UTF8.GetString(ReadSequenceToArray(redelivered.Body)).Should().Be("{\"n\":1}");
    }

    [Fact]
    public async Task SettleAsync_RejectWithoutDlq_CommitsPastRejectedMessage()
    {
        // Arrange — retry/DLQ disabled: Reject is a terminal forward settlement (Warning logged)
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"t-reject-{suffix}";
        string groupId = $"grp-{suffix}";

        await using (KafkaTransportAdapter first = CreateAdapter(groupId))
        {
            await DeployTopicAsync(first, topicName, partitions: 1, cts.Token);
            await first.SendBatchAsync(
                [MakeMessage(topicName, "{\"n\":1}"), MakeMessage(topicName, "{\"n\":2}")],
                cts.Token);

            // Act — reject offset 0, ack offset 1
            List<string> bodies = await RunSessionAsync(
                first, topicName, [SettlementAction.Reject, SettlementAction.Ack], cts.Token);
            bodies.Should().Equal("{\"n\":1}", "{\"n\":2}");

            await first.SendBatchAsync([MakeMessage(topicName, "{\"n\":3}")], cts.Token);
        }

        // Assert — the rejected message is not redelivered; the first delivery is the new message
        await using KafkaTransportAdapter second = CreateAdapter(groupId);
        using CancellationTokenSource redeliveryCts = new(TimeSpan.FromSeconds(20));
        InboundMessage next = await ConsumeOneAsync(second, topicName, redeliveryCts.Token);
        Encoding.UTF8.GetString(ReadSequenceToArray(next.Body)).Should().Be("{\"n\":3}");
    }
}
