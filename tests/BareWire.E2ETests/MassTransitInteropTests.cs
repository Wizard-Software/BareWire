using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.E2ETests.Helpers;
using Xunit;

namespace BareWire.E2ETests;

/// <summary>
/// E2E scenarios for MassTransit interop — verifies that BareWire can consume both
/// MassTransit-envelope and raw JSON messages on the same broker via content-type routing.
/// </summary>
public sealed class MassTransitInteropTests(SamplesAppFixture fixture) : IClassFixture<SamplesAppFixture>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    // ── E2E-015: MassTransit envelope message consumed ─────────────────────────

    [Fact]
    public async Task E2E015_MassTransitInterop_EnvelopeMessageConsumed()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("masstransit-interop");

        // Act — trigger a MassTransit-envelope publish via the simulator
        var response = await client.PostAsync("/masstransit/simulate", null);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Assert — poll until a MassTransit-sourced order appears in the processed store
        var orders = await client.PollUntilAsync<JsonElement[]>(
            "/orders/processed",
            items => items.Any(i => i.GetProperty("source").GetString() == "MassTransit"),
            PollTimeout);

        var mtOrder = orders.First(i => i.GetProperty("source").GetString() == "MassTransit");
        mtOrder.GetProperty("orderId").GetString().Should().NotBeNullOrEmpty();
        mtOrder.GetProperty("amount").GetDecimal().Should().Be(99.99m);
        mtOrder.GetProperty("currency").GetString().Should().Be("PLN");
    }

    // ── E2E-016: BareWire raw JSON message consumed ────────────────────────────

    [Fact]
    public async Task E2E016_MassTransitInterop_RawJsonMessageConsumed()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("masstransit-interop");

        // Act — publish a raw JSON OrderCreated via IBus
        var response = await client.PostAsync("/barewire/publish", null);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        string orderId = body.GetProperty("orderId").GetString()!;

        // Assert — poll until the specific BareWire-sourced order appears
        var orders = await client.PollUntilAsync<JsonElement[]>(
            "/orders/processed",
            items => items.Any(i =>
                i.GetProperty("source").GetString() == "BareWire"
                && i.GetProperty("orderId").GetString() == orderId),
            PollTimeout);

        var bwOrder = orders.First(i =>
            i.GetProperty("source").GetString() == "BareWire"
            && i.GetProperty("orderId").GetString() == orderId);
        bwOrder.GetProperty("amount").GetDecimal().Should().Be(99.99m);
        bwOrder.GetProperty("currency").GetString().Should().Be("PLN");
    }

    // ── E2E-017: Both formats coexist on same broker ───────────────────────────

    [Fact]
    public async Task E2E017_MassTransitInterop_BothFormatsCoexistOnSameBroker()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("masstransit-interop");

        // Act — publish both formats
        var mtResponse = await client.PostAsync("/masstransit/simulate", null);
        mtResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var bwResponse = await client.PostAsync("/barewire/publish", null);
        bwResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var bwBody = await bwResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        string bwOrderId = bwBody.GetProperty("orderId").GetString()!;

        // Assert — poll until both sources are represented
        var orders = await client.PollUntilAsync<JsonElement[]>(
            "/orders/processed",
            items =>
                items.Any(i => i.GetProperty("source").GetString() == "MassTransit")
                && items.Any(i =>
                    i.GetProperty("source").GetString() == "BareWire"
                    && i.GetProperty("orderId").GetString() == bwOrderId),
            PollTimeout);

        orders.Should().Contain(i => i.GetProperty("source").GetString() == "MassTransit");
        orders.Should().Contain(i =>
            i.GetProperty("source").GetString() == "BareWire"
            && i.GetProperty("orderId").GetString() == bwOrderId);
    }

    // ── E2E-018: Background simulator produces messages ────────────────────────

    [Fact]
    public async Task E2E018_MassTransitInterop_BackgroundSimulatorProducesMessages()
    {
        // Arrange — no explicit publish needed; MassTransitSimulator BackgroundService
        // publishes every 5 seconds automatically after startup.
        using var client = fixture.CreateHttpClient("masstransit-interop");

        // Assert — poll until at least 2 MassTransit-sourced messages have accumulated
        var orders = await client.PollUntilAsync<JsonElement[]>(
            "/orders/processed",
            items => items.Count(i => i.GetProperty("source").GetString() == "MassTransit") >= 2,
            PollTimeout);

        orders.Count(i => i.GetProperty("source").GetString() == "MassTransit").Should().BeGreaterThanOrEqualTo(2);
    }
}
