using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using RabbitMQ.Client;
using Xunit;

namespace BareWire.E2ETests;

/// <summary>
/// E2E scenario for the MassTransit publish-only bridge (task 12.15).
///
/// Verifies that <c>POST /masstransit/bridge-publish</c> on the
/// <c>masstransit-interop</c> sample publishes a <c>BridgeOrderCreated</c> message
/// to the <c>mt-bridge-orders</c> fanout exchange with the MassTransit envelope
/// content-type, without requiring any BareWire receive endpoint on that exchange.
///
/// The sample declares <c>mt-bridge-orders</c> as a fanout exchange with no bindings,
/// so this test acts as an external observer: it declares a temporary auto-delete
/// queue, binds it to the exchange, then triggers the publish and consumes the
/// resulting message directly via RabbitMQ.Client.
/// </summary>
public sealed class MassTransitBridgePublishTests(SamplesAppFixture fixture) : IClassFixture<SamplesAppFixture>
{
    private const string BridgeExchange = "mt-bridge-orders";
    private const string MassTransitContentType = "application/vnd.masstransit+json";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    // ── E2E-019: Publish-only bridge emits MassTransit envelope on mt-bridge-orders ──

    [Fact]
    public async Task E2E019_MassTransitBridgePublish_EmitsEnvelopeOnBridgeExchange()
    {
        // Arrange — bind an auto-delete observer queue to mt-bridge-orders so this
        // test can intercept the published envelope without altering the sample's
        // "publish-only" topology.
        using var cts = new CancellationTokenSource(PollTimeout);

        var factory = new ConnectionFactory
        {
            Uri = new Uri(fixture.GetRabbitMqConnectionString()),
            AutomaticRecoveryEnabled = false,
        };

        await using IConnection connection = await factory.CreateConnectionAsync(cts.Token);
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        string observerQueue = $"e2e-bridge-observer-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(
            queue: observerQueue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: null,
            cancellationToken: cts.Token);
        await channel.QueueBindAsync(
            queue: observerQueue,
            exchange: BridgeExchange,
            routingKey: string.Empty,
            cancellationToken: cts.Token);

        // Act — trigger the publish-only bridge endpoint.
        using HttpClient client = fixture.CreateHttpClient("masstransit-interop");
        HttpResponseMessage response = await client.PostAsync("/masstransit/bridge-publish", content: null, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Assert — poll the observer queue until the message arrives, then verify its
        // content-type (MassTransit envelope) and body shape.
        BasicGetResult? delivery = await PollForMessageAsync(channel, observerQueue, cts.Token);

        delivery.Should().NotBeNull("the bridge-publish endpoint should emit a message to mt-bridge-orders");

        delivery!.BasicProperties.ContentType.Should().Be(
            MassTransitContentType,
            "the publish-only bridge must serialize BridgeOrderCreated using the MassTransit envelope format");

        byte[] body = delivery.Body.ToArray();
        string json = Encoding.UTF8.GetString(body);

        using JsonDocument envelope = JsonDocument.Parse(json);
        JsonElement root = envelope.RootElement;

        // A MassTransit envelope has top-level messageId, messageType (array), sentTime, message.
        root.TryGetProperty("messageId", out _).Should().BeTrue("envelope must carry a messageId");
        root.TryGetProperty("messageType", out JsonElement messageType).Should().BeTrue();
        messageType.ValueKind.Should().Be(JsonValueKind.Array);
        messageType[0].GetString().Should().EndWith(
            ":BridgeOrderCreated",
            "the envelope must carry the URN of the BridgeOrderCreated message type");

        root.TryGetProperty("message", out JsonElement inner).Should().BeTrue();
        inner.GetProperty("orderId").GetString().Should().NotBeNullOrEmpty();
        inner.GetProperty("amount").GetDecimal().Should().Be(75.00m);
        inner.GetProperty("currency").GetString().Should().Be("USD");

        // Cleanup — auto-delete queue is removed when the channel closes.
        await channel.QueueUnbindAsync(observerQueue, BridgeExchange, string.Empty, cancellationToken: cts.Token);
    }

    // ── E2E-020: /barewire/publish remains raw JSON (regression for MapSerializer scope) ──

    [Fact]
    public async Task E2E020_MassTransitBridge_DoesNotAffectRawJsonPublishPath()
    {
        // Arrange — observe the raw JSON exchange that /barewire/publish targets.
        using var cts = new CancellationTokenSource(PollTimeout);

        var factory = new ConnectionFactory
        {
            Uri = new Uri(fixture.GetRabbitMqConnectionString()),
            AutomaticRecoveryEnabled = false,
        };

        await using IConnection connection = await factory.CreateConnectionAsync(cts.Token);
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        string observerQueue = $"e2e-raw-observer-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(
            queue: observerQueue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: null,
            cancellationToken: cts.Token);
        await channel.QueueBindAsync(
            queue: observerQueue,
            exchange: "barewire-orders",
            routingKey: string.Empty,
            cancellationToken: cts.Token);

        // Act
        using HttpClient client = fixture.CreateHttpClient("masstransit-interop");
        HttpResponseMessage response = await client.PostAsync("/barewire/publish", content: null, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Assert — the OrderCreated published via /barewire/publish must remain raw JSON
        // (MapSerializer is scoped to BridgeOrderCreated, not OrderCreated).
        BasicGetResult? delivery = await PollForMessageAsync(channel, observerQueue, cts.Token);

        delivery.Should().NotBeNull("/barewire/publish must still emit a message to barewire-orders");
        delivery!.BasicProperties.ContentType.Should().Be(
            "application/json",
            "MapSerializer<BridgeOrderCreated, MT> must not affect OrderCreated serialization (ADR-001 raw-first preserved)");

        // Cleanup
        await channel.QueueUnbindAsync(observerQueue, "barewire-orders", string.Empty, cancellationToken: cts.Token);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<BasicGetResult?> PollForMessageAsync(
        IChannel channel,
        string queueName,
        CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(250);

        while (!cancellationToken.IsCancellationRequested)
        {
            BasicGetResult? result = await channel.BasicGetAsync(queueName, autoAck: true, cancellationToken);
            if (result is not null)
            {
                return result;
            }

            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }
}
