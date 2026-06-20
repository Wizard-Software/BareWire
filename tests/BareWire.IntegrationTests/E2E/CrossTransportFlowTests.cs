using System.Buffers;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.E2E;

// ── Lokalne typy wiadomości ────────────────────────────────────────────────────

/// <summary>Reprezentuje zamówienie używane w testach cross-transport.</summary>
public sealed record CrossTransportOrder(string OrderId, decimal Amount, string Currency);

/// <summary>
/// Testy E2E scenariusza cross-transport: wiadomość publikowana przez jeden adapter RabbitMQ
/// jest odbierana przez drugi niezależny adapter podłączony do tego samego brokera.
///
/// <para>
/// Symuluje przepływ zdarzeń między dwoma oddzielnymi instancjami transportu
/// (np. dwie usługi w jednym klastrze) na żywym brokerze RabbitMQ dostarczonym
/// przez <see cref="AspireFixture"/>. Każdy test używa nazw z sufiksem GUID, by uniknąć
/// kolizji między przebiegami.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class CrossTransportFlowTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Helpery ───────────────────────────────────────────────────────────────

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
                ?? throw new InvalidOperationException($"Nie udało się zdeserializować {typeof(T).Name}.");
        }

        byte[] buffer = new byte[body.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in body)
        {
            segment.Span.CopyTo(buffer.AsSpan(offset));
            offset += segment.Length;
        }

        return JsonSerializer.Deserialize<T>(buffer)
            ?? throw new InvalidOperationException($"Nie udało się zdeserializować {typeof(T).Name}.");
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

        throw new InvalidOperationException("Strumień konsumpcji zakończył się przed dostarczeniem wiadomości.");
    }

    // ── E2E: Cross-transport round-trip ───────────────────────────────────────

    /// <summary>
    /// Weryfikuje, że wiadomość opublikowana przez jeden adapter RabbitMQ (<c>publishAdapter</c>)
    /// jest poprawnie odbierana przez drugi, niezależny adapter (<c>consumeAdapter</c>) podłączony
    /// do tego samego brokera — symulując przepływ cross-transport między dwoma instancjami usługi.
    ///
    /// <para>
    /// Asercje:
    /// <list type="bullet">
    ///   <item>Broker potwierdził publikację (<c>IsConfirmed == true</c>).</item>
    ///   <item>Body deserializuje się do oryginalnych wartości (round-trip <c>OrderId</c>, <c>Amount</c>, <c>Currency</c>).</item>
    ///   <item>Nagłówek <c>content-type</c> jest propagowany przez brokera.</item>
    ///   <item>Co najmniej jeden nagłówek własny (<c>X-Source-Service</c>) dociera do konsumenta.</item>
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

        // Act — publikacja przez publishAdapter, konsumpcja przez consumeAdapter
        IReadOnlyList<SendResult> sendResults = await publishAdapter.SendBatchAsync([outbound], cts.Token);
        InboundMessage received = await ConsumeOneAsync(consumeAdapter, queueName, cts.Token);

        try
        {
            // Assert — broker potwierdził publikację
            sendResults.Should().HaveCount(1);
            sendResults[0].IsConfirmed.Should().BeTrue(
                because: "broker RabbitMQ musi potwierdzić każdą opublikowaną wiadomość");

            // Assert — body round-trip: wszystkie pola muszą być identyczne
            CrossTransportOrder roundTripped = DeserializeFromSequence<CrossTransportOrder>(received.Body);
            roundTripped.OrderId.Should().Be(order.OrderId,
                because: "OrderId musi przeżyć round-trip przez brokera");
            roundTripped.Amount.Should().Be(order.Amount,
                because: "Amount musi przeżyć round-trip przez brokera");
            roundTripped.Currency.Should().Be(order.Currency,
                because: "Currency musi przeżyć round-trip przez brokera");

            // Assert — content-type propagowany przez brokera
            received.Headers.Should().ContainKey("content-type",
                because: "broker musi propagować content-type z OutboundMessage do InboundMessage");
            received.Headers["content-type"].Should().Be("application/json");

            // Assert — nagłówek własny propagowany
            received.Headers.Should().ContainKey("X-Source-Service",
                because: "co najmniej jeden nagłówek własny musi przeżyć przepływ cross-transport");
            received.Headers["X-Source-Service"].Should().Be("service-a");
        }
        finally
        {
            // Rozliczenie i zwolnienie bufora puli — D-3
            await consumeAdapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
            received.Dispose();
        }
    }
}
