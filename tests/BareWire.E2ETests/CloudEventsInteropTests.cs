using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.E2ETests.Helpers;
using Xunit;

namespace BareWire.E2ETests;

/// <summary>
/// E2E scenarios for the CloudEvents interop sample. The sample uses a single fanout exchange
/// bound to three queues (one per consumer), so EVERY publish is broadcast to all three consumers;
/// the per-mode difference is observable only on the READ side:
/// <list type="bullet">
/// <item>binary (<c>PublishCloudEventAsync</c>) → <c>ce-*</c> transport headers, read via <c>GetCloudEvent()</c>;</item>
/// <item>structured (<c>PublishCloudEventStructuredAsync</c>) → <c>application/cloudevents+json</c> envelope unpacked by the Content-Type router, CE attributes live INSIDE the envelope (no <c>ce-*</c> headers);</item>
/// <item>raw (<c>PublishAsync</c>) → plain JSON, no CloudEvents metadata (ADR-001).</item>
/// </list>
/// Assertions read the per-consumer receipts exposed by <c>GET /shipments/processed</c>.
/// </summary>
public sealed class CloudEventsInteropTests(SamplesAppFixture fixture) : IClassFixture<SamplesAppFixture>
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private static object NewShipment(string id) => new
    {
        ShipmentId = id,
        Destination = "Warehouse-A",
        Carrier = "FedEx",
    };

    private static bool IsReceipt(JsonElement r, string shipmentId, string consumer) =>
        r.GetProperty("shipmentId").GetString() == shipmentId
        && r.GetProperty("consumer").GetString() == consumer;

    // ── E2E-021: binary CloudEvents — ce-* headers read by BinaryAwareConsumer ─────

    [Fact]
    public async Task E2E021_CloudEventsBinary_ConsumedWithCeHeaders()
    {
        using var client = fixture.CreateHttpClient("cloudevents-interop");
        string shipmentId = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync("/cloudevents/publish-binary", NewShipment(shipmentId));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // BinaryAwareConsumer reads ce-* headers → records HasCloudEventAttributes=true + Ce* attrs.
        var receipts = await client.PollUntilAsync<JsonElement[]>(
            "/shipments/processed",
            items => items.Any(r =>
                IsReceipt(r, shipmentId, "BinaryAware")
                && r.GetProperty("hasCloudEventAttributes").GetBoolean()),
            PollTimeout);

        var binary = receipts.First(r => IsReceipt(r, shipmentId, "BinaryAware"));
        binary.GetProperty("hasCloudEventAttributes").GetBoolean().Should().BeTrue();
        binary.GetProperty("ceType").GetString().Should().Be("com.barewire.sample.shipment.dispatched");
        binary.GetProperty("ceSource").GetString().Should().Contain("binary");
        binary.GetProperty("ceId").GetString().Should().NotBeNullOrEmpty();
    }

    // ── E2E-022: structured CloudEvents — envelope unpacked, no ce-* transport headers ─

    [Fact]
    public async Task E2E022_CloudEventsStructured_ConsumedViaEnvelopeWithoutCeHeaders()
    {
        using var client = fixture.CreateHttpClient("cloudevents-interop");
        string shipmentId = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync("/cloudevents/publish-structured", NewShipment(shipmentId));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // The Content-Type router unpacks the application/cloudevents+json envelope before the
        // StructuredConsumer runs. CE attributes live inside the envelope, NOT in ce-* headers,
        // so GetCloudEvent() (which reads headers) correctly reports none → HasCloudEventAttributes=false.
        var receipts = await client.PollUntilAsync<JsonElement[]>(
            "/shipments/processed",
            items => items.Any(r => IsReceipt(r, shipmentId, "Structured")),
            PollTimeout);

        var structured = receipts.First(r => IsReceipt(r, shipmentId, "Structured"));
        structured.GetProperty("hasCloudEventAttributes").GetBoolean().Should().BeFalse();
    }

    // ── E2E-023: raw JSON — no CloudEvents metadata at all (ADR-001) ───────────────

    [Fact]
    public async Task E2E023_RawJson_ConsumedWithoutCloudEventMetadata()
    {
        using var client = fixture.CreateHttpClient("cloudevents-interop");
        string shipmentId = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync("/barewire/publish", NewShipment(shipmentId));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var receipts = await client.PollUntilAsync<JsonElement[]>(
            "/shipments/processed",
            items => items.Any(r => IsReceipt(r, shipmentId, "Raw")),
            PollTimeout);

        var raw = receipts.First(r => IsReceipt(r, shipmentId, "Raw"));
        raw.GetProperty("hasCloudEventAttributes").GetBoolean().Should().BeFalse();
    }

    // ── E2E-024: fanout broadcast — all consumers receive; only binary carries ce-* ─

    [Fact]
    public async Task E2E024_FanoutBroadcast_AllConsumersReceive_OnlyBinaryCarriesCeHeaders()
    {
        using var client = fixture.CreateHttpClient("cloudevents-interop");
        string binaryId = Guid.NewGuid().ToString();
        string rawId = Guid.NewGuid().ToString();

        (await client.PostAsJsonAsync("/cloudevents/publish-binary", NewShipment(binaryId)))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await client.PostAsJsonAsync("/barewire/publish", NewShipment(rawId)))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Fanout: each publish must reach all three consumer queues.
        var receipts = await client.PollUntilAsync<JsonElement[]>(
            "/shipments/processed",
            items => ConsumersFor(items, binaryId).Count == 3 && ConsumersFor(items, rawId).Count == 3,
            PollTimeout);

        // Binary frame broadcast to all queues → every consumer observed ce-* headers.
        receipts.Where(r => r.GetProperty("shipmentId").GetString() == binaryId)
            .Should().OnlyContain(r => r.GetProperty("hasCloudEventAttributes").GetBoolean());
        // Raw frame → no consumer observed CloudEvents metadata.
        receipts.Where(r => r.GetProperty("shipmentId").GetString() == rawId)
            .Should().OnlyContain(r => !r.GetProperty("hasCloudEventAttributes").GetBoolean());
    }

    private static HashSet<string> ConsumersFor(JsonElement[] items, string shipmentId) =>
        items.Where(r => r.GetProperty("shipmentId").GetString() == shipmentId)
            .Select(r => r.GetProperty("consumer").GetString()!)
            .ToHashSet();
}
