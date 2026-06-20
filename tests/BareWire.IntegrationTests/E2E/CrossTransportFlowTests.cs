using System.Buffers;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.E2E;

// ── Local message types ───────────────────────────────────────────────────────

/// <summary>Represents an order used in cross-transport tests.</summary>
public sealed record CrossTransportOrder(string OrderId, decimal Amount, string Currency);

/// <summary>
/// E2E tests for the cross-transport scenario: a message published by one RabbitMQ adapter
/// is received by a second, independent adapter connected to the same broker.
///
/// <para>
/// Simulates event flow between two separate transport instances
/// (e.g. two services in the same cluster) against a live RabbitMQ broker provided
/// by <see cref="AspireFixture"/>. Each test uses GUID-suffixed names to avoid
/// collisions across runs.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class CrossTransportFlowTests(AspireFixture fixture)
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

    private static async Task<(string ExchangeName, string QueueName)> DeploySimpleTopologyAsync(
        RabbitMqTransportAdapter adapter,
        string suffix,
        CancellationToken ct)
    {
        string exchangeName = $"e2e-ct-ex-{suffix}";
        string queueName = $"e2e-ct-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);
        await adapter.DeployTopologyAsync(configurator.Build(), ct);

        return (exchangeName, queueName);
    }

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

    private static FlowControlOptions StandardFlow() =>
        new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

    private static async Task<InboundMessage> ConsumeOneAsync(
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
    {
        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, StandardFlow(), ct))
        {
            return msg;
        }

        throw new InvalidOperationException("The consumption stream ended before a message was delivered.");
    }

    // ── E2E: Cross-transport round-trip ───────────────────────────────────────

    /// <summary>
    /// Verifies that a message published by one RabbitMQ adapter (<c>publishAdapter</c>)
    /// is correctly received by a second, independent adapter (<c>consumeAdapter</c>) connected
    /// to the same broker — simulating a cross-transport flow between two service instances.
    ///
    /// <para>
    /// Assertions:
    /// <list type="bullet">
    ///   <item>The broker confirmed the publication (<c>IsConfirmed == true</c>).</item>
    ///   <item>The body deserialises to the original values (round-trip <c>OrderId</c>, <c>Amount</c>, <c>Currency</c>).</item>
    ///   <item>The <c>content-type</c> header is propagated by the broker.</item>
    ///   <item>At least one custom header (<c>X-Source-Service</c>) reaches the consumer.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public async Task CrossTransportFlow_PublishOnOneAdapter_ConsumedByAnother()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        await using RabbitMqTransportAdapter publishAdapter = CreateAdapter();
        await using RabbitMqTransportAdapter consumeAdapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) = await DeploySimpleTopologyAsync(publishAdapter, suffix, cts.Token);

        var order = new CrossTransportOrder(
            OrderId: $"ORD-CT-{suffix[..8].ToUpperInvariant()}",
            Amount: 299.49m,
            Currency: "PLN");

        byte[] body = SerializeToJson(order);

        OutboundMessage outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
                ["X-Source-Service"] = "service-a",
            },
            body: body,
            contentType: "application/json");

        // Act — publish via publishAdapter, consume via consumeAdapter
        IReadOnlyList<SendResult> sendResults = await publishAdapter.SendBatchAsync([outbound], cts.Token);
        InboundMessage received = await ConsumeOneAsync(consumeAdapter, queueName, cts.Token);

        try
        {
            // Assert — broker confirmed the publication
            sendResults.Should().HaveCount(1);
            sendResults[0].IsConfirmed.Should().BeTrue(
                because: "the RabbitMQ broker must confirm every published message");

            // Assert — body round-trip: all fields must be identical
            CrossTransportOrder roundTripped = DeserializeFromSequence<CrossTransportOrder>(received.Body);
            roundTripped.OrderId.Should().Be(order.OrderId,
                because: "OrderId must survive a round-trip through the broker");
            roundTripped.Amount.Should().Be(order.Amount,
                because: "Amount must survive a round-trip through the broker");
            roundTripped.Currency.Should().Be(order.Currency,
                because: "Currency must survive a round-trip through the broker");

            // Assert — content-type propagated by the broker
            received.Headers.Should().ContainKey("content-type",
                because: "the broker must propagate content-type from OutboundMessage to InboundMessage");
            received.Headers["content-type"].Should().Be("application/json");

            // Assert — custom header propagated
            received.Headers.Should().ContainKey("X-Source-Service",
                because: "at least one custom header must survive the cross-transport flow");
            received.Headers["X-Source-Service"].Should().Be("service-a");
        }
        finally
        {
            // Settlement and pool-buffer release — D-3
            await consumeAdapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
            received.Dispose();
        }
    }
}
