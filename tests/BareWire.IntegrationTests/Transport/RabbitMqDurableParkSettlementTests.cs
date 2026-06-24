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
/// Integration tests for <see cref="IDurableParkSettlement.ParkHeadDurablyAsync"/> via
/// <see cref="RabbitMqTransportAdapter"/>.
/// Each test uses a real RabbitMQ instance provisioned via <see cref="AspireFixture"/>.
/// Every test creates its own isolated topology with unique queue names to prevent interference.
/// </summary>
public sealed class RabbitMqDurableParkSettlementTests(AspireFixture fixture)
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

    private static OutboundMessage MakeMessage(string queueName, string payload = "{\"ok\":true}") =>
        new(
            routingKey: queueName,
            headers: new Dictionary<string, string> { ["BW-Exchange"] = string.Empty },
            body: Encoding.UTF8.GetBytes(payload),
            contentType: "application/json");

    /// <summary>
    /// Reads exactly one message from the consume stream, with a 30-second timeout.
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

    private static byte[] ReadSequenceToArray(ReadOnlySequence<byte> seq)
    {
        if (seq.IsSingleSegment)
        {
            return seq.FirstSpan.ToArray();
        }

        byte[] buf = new byte[seq.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> seg in seq)
        {
            seg.Span.CopyTo(buf.AsSpan(offset));
            offset += seg.Length;
        }

        return buf;
    }

    // ── Durable-ack happy path ─────────────────────────────────────────────────

    /// <summary>
    /// ParkHeadDurablyAsync re-publishes the head to a DLX on a confirm channel and returns
    /// IsDurablyConfirmed=true. After the call: a copy exists in the DLQ and the original is
    /// gone from the source queue (ACKed).
    /// </summary>
    [Fact]
    public async Task ParkHeadAsync_WhenHeadPublishedToDlx_ReturnsConfirmedDurableSettlement()
    {
        // Arrange — unique names per test run to prevent cross-test interference.
        string id = Guid.NewGuid().ToString("N");
        string srcQueue = $"test-dpark-src-{id}";
        string dlxName = $"test-dpark-dlx-{id}";
        string dlqName = $"test-dpark-dlq-{id}";

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        // Deploy topology: source queue → DLX → DLQ
        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareQueue(srcQueue, durable: false, autoDelete: false);
        configurator.DeclareExchange(dlxName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(dlqName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(dlxName, dlqName, routingKey: dlqName);
        await adapter.DeployTopologyAsync(configurator.Build(), cts.Token);

        // Publish one message to the source queue.
        byte[] payload = Encoding.UTF8.GetBytes("{\"poison\":true}");
        await adapter.SendBatchAsync([MakeMessage(srcQueue, "{\"poison\":true}")], cts.Token);

        // Consume it to obtain the InboundMessage with a live delivery tag.
        InboundMessage head = await ConsumeOneAsync(adapter, srcQueue, cts.Token);

        // Act — call ParkHeadDurablyAsync directly on the adapter.
        // InternalsVisibleTo grants access to both the internal IDurableParkSettlement interface
        // and to the public implementation method on RabbitMqTransportAdapter.
        DurableSettlementResult result = await adapter.ParkHeadDurablyAsync(
            head,
            deadLetterExchange: dlxName,
            deadLetterRoutingKey: dlqName,
            cancellationToken: cts.Token);

        // Assert 1 — settlement is durably confirmed.
        result.IsDurablyConfirmed.Should().BeTrue();
        result.FailureReason.Should().BeNull();

        // Assert 2 — a copy of the message arrived in the DLQ.
        await using RabbitMqTransportAdapter dlqAdapter = CreateAdapter();
        InboundMessage dlqMsg = await ConsumeOneAsync(dlqAdapter, dlqName, cts.Token);
        ReadSequenceToArray(dlqMsg.Body).Should().BeEquivalentTo(payload);

        // ACK the DLQ message so we leave the queue clean.
        await dlqAdapter.SettleAsync(SettlementAction.Ack, dlqMsg, cts.Token);
    }

    // ── Failed-settle: DLX exists but has no bound queue ──────────────────────

    /// <summary>
    /// ParkHeadDurablyAsync returns IsDurablyConfirmed=false when the dead-letter exchange
    /// exists but has no bound queue. The original message must NOT be ACKed — it remains in
    /// the source queue (head stays, per-key ordering unbroken, C3 invariant).
    /// </summary>
    [Fact]
    public async Task ParkHeadAsync_WhenDlxHasNoBoundQueue_ReturnsFailed_AndOriginalStaysInQueue()
    {
        // Arrange — unique names.
        string id = Guid.NewGuid().ToString("N");
        string srcQueue = $"test-dpark-nobind-src-{id}";
        string dlxName = $"test-dpark-nobind-dlx-{id}";

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        // Deploy: source queue + DLX with NO bound queue — mandatory publish will be returned.
        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareQueue(srcQueue, durable: false, autoDelete: false);
        configurator.DeclareExchange(dlxName, ExchangeType.Direct, durable: false, autoDelete: false);
        await adapter.DeployTopologyAsync(configurator.Build(), cts.Token);

        // Publish and consume the head.
        await adapter.SendBatchAsync([MakeMessage(srcQueue)], cts.Token);
        InboundMessage head = await ConsumeOneAsync(adapter, srcQueue, cts.Token);

        // Act — mandatory publish to DLX with no binding must surface as failed-settle.
        DurableSettlementResult result = await adapter.ParkHeadDurablyAsync(
            head,
            deadLetterExchange: dlxName,
            deadLetterRoutingKey: "no-such-queue",
            cancellationToken: cts.Token);

        // Assert 1 — failed settlement.
        result.IsDurablyConfirmed.Should().BeFalse();
        result.FailureReason.Should().NotBeNullOrEmpty();

        // Assert 2 — the original was NOT ACKed; head still in the source queue.
        // We verify by consuming again with a fresh adapter (same connection, new channel).
        // Because the previous delivery is still unacked we NACK it first so it is requeued,
        // then consume with a fresh adapter to confirm it re-appears.
        await adapter.SettleAsync(SettlementAction.Requeue, head, cts.Token);

        await using RabbitMqTransportAdapter verifyAdapter = CreateAdapter();
        InboundMessage redelivered = await ConsumeOneAsync(verifyAdapter, srcQueue, cts.Token);

        redelivered.Should().NotBeNull("the original message must still be present in the source queue");

        // Clean up — ACK the redelivered message.
        await verifyAdapter.SettleAsync(SettlementAction.Ack, redelivered, cts.Token);
    }
}
