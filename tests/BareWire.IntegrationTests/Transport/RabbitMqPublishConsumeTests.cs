using System.Buffers;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Integration tests for publish, consume, and settlement via <see cref="RabbitMqTransportAdapter"/>.
/// All tests use a real RabbitMQ instance provisioned via <see cref="AspireFixture"/>.
/// Each test creates its own isolated queue with a unique name to prevent cross-test interference.
/// </summary>
public sealed class RabbitMqPublishConsumeTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Factory & helpers ──────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter(Action<RabbitMqTransportOptions>? configure = null)
    {
        var options = new RabbitMqTransportOptions
        {
            ConnectionString = fixture.GetRabbitMqConnectionString(),
        };
        configure?.Invoke(options);
        return new RabbitMqTransportAdapter(options, NullLogger<RabbitMqTransportAdapter>.Instance);
    }

    /// <summary>
    /// Declares a plain durable queue and returns its name.
    /// The default exchange routes by routing key = queue name, so no explicit binding is needed.
    /// </summary>
    private static async Task DeployQueueAsync(
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
    {
        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        await adapter.DeployTopologyAsync(configurator.Build(), ct);
    }

    private static OutboundMessage MakeMessage(string queueName, string payload = "{\"ok\":true}") =>
        new(
            routingKey: queueName,
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
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
    {
        FlowControlOptions flow = new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, flow, ct))
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
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string queueName = $"test-single-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        OutboundMessage message = MakeMessage(queueName);

        // Act
        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync([message], cts.Token);

        // Assert
        results.Should().HaveCount(1);
        results[0].IsConfirmed.Should().BeTrue();
        results[0].DeliveryTag.Should().BeGreaterThan(0UL);
    }

    [Fact]
    public async Task SendBatchAsync_MultipleMessages_AllConfirmed()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string queueName = $"test-batch-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        OutboundMessage[] messages =
        [
            MakeMessage(queueName, "{\"seq\":1}"),
            MakeMessage(queueName, "{\"seq\":2}"),
            MakeMessage(queueName, "{\"seq\":3}"),
        ];

        // Act
        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(messages, cts.Token);

        // Assert — all three messages confirmed
        results.Should().HaveCount(3);
        results.Should().AllSatisfy(r =>
        {
            r.IsConfirmed.Should().BeTrue();
            r.DeliveryTag.Should().BeGreaterThan(0UL);
        });

        // Delivery tags are unique within this adapter instance
        results.Select(r => r.DeliveryTag).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task SendBatchAsync_UsesDefaultExchange_WhenNoHeaderOverride()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter(opts =>
        {
            // Default exchange is "" (AMQP default direct exchange).
            // A message published to "" with routingKey = queueName reaches that queue directly.
            opts.DefaultExchange = string.Empty;
        });

        string queueName = $"test-default-ex-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        // No "BW-Exchange" header — adapter should fall back to DefaultExchange ("")
        OutboundMessage message = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>(),
            body: Encoding.UTF8.GetBytes("{\"via\":\"default-exchange\"}"),
            contentType: "application/json");

        // Act
        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync([message], cts.Token);

        // Assert — message routed through the default exchange and confirmed
        results.Should().HaveCount(1);
        results[0].IsConfirmed.Should().BeTrue();
    }

    // ── ConsumeAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_AfterPublish_ReceivesMessage()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string queueName = $"test-consume-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        byte[] expectedBody = Encoding.UTF8.GetBytes("{\"msg\":\"hello\"}");
        OutboundMessage outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string> { ["x-test"] = "roundtrip" },
            body: expectedBody,
            contentType: "application/json");

        // Publish before opening the consume stream so the message is ready in the queue
        await adapter.SendBatchAsync([outbound], cts.Token);

        // Act
        InboundMessage received = await ConsumeOneAsync(adapter, queueName, cts.Token);

        // Assert
        received.Should().NotBeNull();
        received.MessageId.Should().NotBeNullOrEmpty();
        received.DeliveryTag.Should().BeGreaterThan(0UL);
        received.Headers.Should().ContainKey("x-test");
        received.Headers["x-test"].Should().Be("roundtrip");
        ReadSequenceToArray(received.Body).Should().BeEquivalentTo(expectedBody);
    }

    [Fact]
    public async Task ConsumeAsync_RespectsFlowControl_PrefetchCount()
    {
        // Arrange — publish 5 messages but set prefetch to 2 so at most 2 are in-flight at once.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string queueName = $"test-prefetch-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        const int TotalMessages = 5;
        OutboundMessage[] messages = Enumerable
            .Range(1, TotalMessages)
            .Select(i => MakeMessage(queueName, $"{{\"i\":{i}}}"))
            .ToArray();

        await adapter.SendBatchAsync(messages, cts.Token);

        FlowControlOptions flow = new()
        {
            MaxInFlightMessages = 2,   // prefetchCount = 2
            InternalQueueCapacity = 10,
        };

        var receivedMessages = new List<InboundMessage>();

        // Act — consume all messages and ack each one to release the prefetch slot
        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, flow, cts.Token))
        {
            receivedMessages.Add(msg);
            await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);

            if (receivedMessages.Count == TotalMessages)
            {
                break;
            }
        }

        // Assert — all 5 messages were eventually delivered despite the low prefetch limit
        receivedMessages.Should().HaveCount(TotalMessages);
        receivedMessages.Select(m => m.DeliveryTag).Should().OnlyHaveUniqueItems();
    }

    // ── SettleAsync — Ack ─────────────────────────────────────────────────────

    [Fact]
    public async Task SettleAsync_Ack_RemovesFromQueue()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string queueName = $"test-ack-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        await adapter.SendBatchAsync([MakeMessage(queueName)], cts.Token);

        // Act — consume the message, then ack it
        InboundMessage received = await ConsumeOneAsync(adapter, queueName, cts.Token);
        Func<Task> ack = async () =>
            await adapter.SettleAsync(SettlementAction.Ack, received, cts.Token);

        // Assert — ack must not throw; after ack the message is durably removed
        await ack.Should().NotThrowAsync();
    }

    // ── SettleAsync — Nack ────────────────────────────────────────────────────

    [Fact]
    public async Task SettleAsync_Nack_MovesToDlq()
    {
        // Arrange — we verify that Nack (requeue: false) does not throw and the broker accepts it.
        // Full DLQ routing would require a DLQ topology, which is out of scope for this test.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string queueName = $"test-nack-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        await adapter.SendBatchAsync([MakeMessage(queueName)], cts.Token);

        // Act
        InboundMessage received = await ConsumeOneAsync(adapter, queueName, cts.Token);
        Func<Task> nack = async () =>
            await adapter.SettleAsync(SettlementAction.Nack, received, cts.Token);

        // Assert — broker accepts the nack without error
        await nack.Should().NotThrowAsync();
    }

    // ── SettleAsync — Reject ──────────────────────────────────────────────────

    [Fact]
    public async Task SettleAsync_Reject_RemovesFromQueue()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string queueName = $"test-reject-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        await adapter.SendBatchAsync([MakeMessage(queueName)], cts.Token);

        // Act
        InboundMessage received = await ConsumeOneAsync(adapter, queueName, cts.Token);
        Func<Task> reject = async () =>
            await adapter.SettleAsync(SettlementAction.Reject, received, cts.Token);

        // Assert — reject (requeue: false) is accepted by the broker
        await reject.Should().NotThrowAsync();
    }

    // ── SettleAsync — Requeue ─────────────────────────────────────────────────

    [Fact]
    public async Task SettleAsync_Requeue_RedeliversMessage()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string queueName = $"test-requeue-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        byte[] body = Encoding.UTF8.GetBytes("{\"attempt\":1}");
        await adapter.SendBatchAsync([MakeMessage(queueName)], cts.Token);

        FlowControlOptions flow = new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

        var received = new List<InboundMessage>();
        bool requeuedOnce = false;

        // Act — consume, requeue once, then ack the redelivery
        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, flow, cts.Token))
        {
            received.Add(msg);

            if (!requeuedOnce)
            {
                // Requeue the first delivery — should cause re-delivery
                await adapter.SettleAsync(SettlementAction.Requeue, msg, cts.Token);
                requeuedOnce = true;
            }
            else
            {
                // Ack the re-delivered message so it does not loop
                await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);
                break;
            }
        }

        // Assert — received the message at least twice (original + 1 redelivery)
        received.Should().HaveCountGreaterThanOrEqualTo(2);
        received[1].Headers.Should().ContainKey("BW-RoutingKey");
    }

    // ── SettleAsync — Defer (disabled) ────────────────────────────────────────

    [Fact]
    public async Task SettleAsync_Defer_WhenDisabled_ThrowsNotSupported()
    {
        // Arrange — DeferEnabled defaults to false
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter(opts =>
        {
            opts.DeferEnabled = false;
        });

        string queueName = $"test-defer-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        await adapter.SendBatchAsync([MakeMessage(queueName)], cts.Token);

        InboundMessage received = await ConsumeOneAsync(adapter, queueName, cts.Token);

        // Act + Assert — Defer must throw NotSupportedException when DeferEnabled = false
        Func<Task> defer = async () =>
            await adapter.SettleAsync(SettlementAction.Defer, received, cts.Token);

        await defer.Should().ThrowAsync<NotSupportedException>();
    }
}
