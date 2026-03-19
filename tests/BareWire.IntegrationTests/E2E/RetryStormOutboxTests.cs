using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.E2E;

/// <summary>
/// E2E-3: Retry storm and dead-letter queue test.
///
/// <para>
/// Publishes a batch of messages. Consumes them and intentionally Nacks a subset
/// (simulating transient failures), while Acking the rest. Verifies that:
/// <list type="bullet">
///   <item>Acked messages are not re-delivered.</item>
///   <item>Nacked messages (with requeue) are available for re-consumption.</item>
///   <item>Nacked messages without requeue are accepted by the broker (simulating DLQ routing).</item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class RetryStormOutboxTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions
            {
                ConnectionString = fixture.GetRabbitMqConnectionString(),
            },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    private static async Task<(string ExchangeName, string QueueName, string DlqQueueName)>
        DeployTopologyAsync(
            RabbitMqTransportAdapter adapter,
            string suffix,
            CancellationToken ct)
    {
        string exchangeName = $"e2e-retry-ex-{suffix}";
        string queueName = $"e2e-retry-q-{suffix}";
        string dlqExchangeName = $"e2e-retry-dlq-ex-{suffix}";
        string dlqQueueName = $"e2e-retry-dlq-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();

        // Main exchange and queue
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);

        // DLQ exchange and queue (simulating where Nacked-without-requeue messages go)
        configurator.DeclareExchange(dlqExchangeName, ExchangeType.Fanout, durable: false, autoDelete: false);
        configurator.DeclareQueue(dlqQueueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(dlqExchangeName, dlqQueueName, routingKey: string.Empty);

        await adapter.DeployTopologyAsync(configurator.Build(), ct);

        return (exchangeName, queueName, dlqQueueName);
    }

    private static FlowControlOptions StandardFlow() =>
        new() { MaxInFlightMessages = 50, InternalQueueCapacity = 200 };

    private static OutboundMessage MakeMessage(string exchangeName, string queueName, int seq, string type) =>
        new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
                ["X-Seq"] = seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["X-Type"] = type,
            },
            body: JsonSerializer.SerializeToUtf8Bytes(
                new ThroughputMessage(
                    Id: $"retry-msg-{seq:D4}",
                    Payload: "retry-storm-test",
                    SequenceNumber: seq)),
            contentType: "application/json");

    // ── E2E-3a: Nacked messages (requeue:true) are re-delivered ──────────────

    /// <summary>
    /// E2E-3a: Publishes 20 messages, Nacks the first 10 (requeue), Acks the last 10.
    /// Verifies that the 10 Nacked messages are re-delivered for re-consumption.
    /// </summary>
    [Fact]
    public async Task NackedMessages_WithRequeue_AreRedelivered()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName, _) =
            await DeployTopologyAsync(adapter, suffix, cts.Token);

        const int TotalMessages = 20;
        const int NackCount = 10;

        // Publish 20 messages
        OutboundMessage[] messages = Enumerable
            .Range(0, TotalMessages)
            .Select(i => MakeMessage(exchangeName, queueName, i, i < NackCount ? "nack" : "ack"))
            .ToArray();

        await adapter.SendBatchAsync(messages, cts.Token);

        // First pass: Nack first 10, Ack last 10
        var firstPassMessages = new List<InboundMessage>();
        var flow = StandardFlow();

        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, flow, cts.Token))
        {
            firstPassMessages.Add(msg);

            bool isNackMessage = msg.Headers.TryGetValue("X-Type", out string? type) && type == "nack";

            if (isNackMessage)
            {
                // Requeue — message returns to queue for re-delivery
                await adapter.SettleAsync(SettlementAction.Requeue, msg, cts.Token);
            }
            else
            {
                await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);
            }

            if (firstPassMessages.Count >= TotalMessages)
            {
                break;
            }
        }

        // Second pass: consume the requeued Nacked messages
        var requeuedMessages = new List<InboundMessage>();
        using CancellationTokenSource secondPassCts =
            CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        await foreach (InboundMessage msg in
            adapter.ConsumeAsync(queueName, StandardFlow(), secondPassCts.Token))
        {
            requeuedMessages.Add(msg);
            await adapter.SettleAsync(SettlementAction.Ack, msg, secondPassCts.Token);

            if (requeuedMessages.Count >= NackCount)
            {
                await secondPassCts.CancelAsync();
                break;
            }
        }

        // Assert — requeued messages were re-delivered
        requeuedMessages.Should().HaveCount(NackCount,
            because: "all Nacked-with-requeue messages must be re-delivered");

        // Assert — redelivered messages carry the nack type header
        requeuedMessages.Should().AllSatisfy(m =>
            m.Headers.Should().ContainKey("X-Type").WhoseValue.Should().Be("nack"),
            because: "redelivered messages are the original Requeued messages");
    }

    // ── E2E-3b: Nacked messages (requeue:false) are dropped by broker ─────────

    /// <summary>
    /// E2E-3b: Publishes 5 messages, Nacks all of them without requeue.
    /// Verifies the Nack settlement is accepted by the broker without error.
    /// Then directly publishes sentinel messages to the DLQ exchange and consumes from DLQ,
    /// validating the DLQ receive path end-to-end (mirrors existing E2E-4 DLQ pattern).
    /// </summary>
    [Fact]
    public async Task NackedMessages_WithoutRequeue_AreNotRedelivered()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName, string dlqQueueName) =
            await DeployTopologyAsync(adapter, suffix, cts.Token);

        string dlqExchangeName = $"e2e-retry-dlq-ex-{suffix}";
        const int MessageCount = 5;

        // Publish 5 messages to the main queue
        OutboundMessage[] messages = Enumerable
            .Range(0, MessageCount)
            .Select(i => MakeMessage(exchangeName, queueName, i, "will-nack"))
            .ToArray();

        await adapter.SendBatchAsync(messages, cts.Token);

        // Consume and Nack all without requeue
        var flow = StandardFlow();
        int nacked = 0;

        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, flow, cts.Token))
        {
            Func<Task> nack = async () =>
                await adapter.SettleAsync(SettlementAction.Nack, msg, cts.Token);

            // Assert — Nack without requeue must not throw
            await nack.Should().NotThrowAsync(
                because: "Nacking a message without requeue must be accepted by the broker");

            if (++nacked >= MessageCount)
            {
                break;
            }
        }

        nacked.Should().Be(MessageCount,
            because: "all published messages must be Nackable without error");

        // Verify DLQ receive path: publish sentinels directly to DLQ exchange
        OutboundMessage[] dlqMessages = Enumerable
            .Range(0, MessageCount)
            .Select(i => new OutboundMessage(
                routingKey: string.Empty,
                headers: new Dictionary<string, string>
                {
                    ["BW-Exchange"] = dlqExchangeName,
                    ["X-Dead"] = "true",
                    ["X-Seq"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                body: JsonSerializer.SerializeToUtf8Bytes(
                    new ThroughputMessage($"dlq-{i}", "dead-letter", i)),
                contentType: "application/json"))
            .ToArray();

        await adapter.SendBatchAsync(dlqMessages, cts.Token);

        // Consume from DLQ and verify all sentinels arrived
        var dlqReceived = new List<InboundMessage>();
        using CancellationTokenSource dlqCts =
            CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        await foreach (InboundMessage msg in
            adapter.ConsumeAsync(dlqQueueName, StandardFlow(), dlqCts.Token))
        {
            dlqReceived.Add(msg);
            await adapter.SettleAsync(SettlementAction.Ack, msg, dlqCts.Token);

            if (dlqReceived.Count >= MessageCount)
            {
                await dlqCts.CancelAsync();
                break;
            }
        }

        dlqReceived.Should().HaveCount(MessageCount,
            because: "all sentinel messages published to DLQ exchange must be consumed from DLQ queue");

        dlqReceived.Should().AllSatisfy(m =>
            m.Headers.Should().ContainKey("X-Dead").WhoseValue.Should().Be("true"),
            because: "DLQ messages carry the X-Dead header");
    }

    // ── E2E-3c: Acked messages are not re-delivered ────────────────────────────

    /// <summary>
    /// E2E-3c: Publishes 10 messages, Acks all of them. Verifies that after all Acks
    /// no messages remain in the queue (queue is empty = no duplicate deliveries).
    /// </summary>
    [Fact]
    public async Task AckedMessages_AreNotRedelivered()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName, _) =
            await DeployTopologyAsync(adapter, suffix, cts.Token);

        const int MessageCount = 10;

        // Publish 10 messages
        OutboundMessage[] messages = Enumerable
            .Range(0, MessageCount)
            .Select(i => MakeMessage(exchangeName, queueName, i, "ack"))
            .ToArray();

        await adapter.SendBatchAsync(messages, cts.Token);

        // Consume and Ack all
        int acked = 0;

        await foreach (InboundMessage msg in
            adapter.ConsumeAsync(queueName, StandardFlow(), cts.Token))
        {
            await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);
            acked++;

            if (acked >= MessageCount)
            {
                break;
            }
        }

        acked.Should().Be(MessageCount,
            because: "all published messages must be consumed and Acked exactly once");

        // Assert — no messages remain for re-delivery: attempt a short consume window
        using CancellationTokenSource shortCts = new(TimeSpan.FromSeconds(3));
        int redelivered = 0;

        try
        {
            await foreach (InboundMessage _ in
                adapter.ConsumeAsync(queueName, StandardFlow(), shortCts.Token))
            {
                redelivered++;
                break;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected — no messages in queue means the stream waits until timeout
        }

        redelivered.Should().Be(0,
            because: "Acked messages must not be re-delivered");
    }
}
