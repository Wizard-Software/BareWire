using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace BareWire.E2ETests.ConsumerRoutingKeys;

/// <summary>
/// Smoke E2E test for the BareWire.Samples.ConsumerRoutingKeys sample.
/// Boots the full AppHost (RabbitMQ + all samples) via <see cref="SamplesAppFixture"/>,
/// then exercises POST /run and asserts three routing behaviors:
/// <list type="bullet">
///   <item>Most-specific-wins: routing key <c>transfer.eu.priority</c> (exact) beats
///         <c>transfer.eu.*</c> (wildcard) — dispatched to <c>PriorityTransferConsumer</c>.</item>
///   <item>Multi-consumer routing: routing key <c>transfer.eu.standard</c> matches only
///         <c>transfer.eu.*</c> — dispatched to <c>RegionTransferConsumer</c>.</item>
///   <item>Type-less interop: routing key <c>legacy.audit.created</c> with no BW-MessageType header
///         dispatched to <c>LegacyNotificationConsumer</c> (AcceptUntyped opt-in);
///         echo is non-vacuous (proves raw-first deserialization into LegacyNotification worked).</item>
/// </list>
/// </summary>
[Trait("Category", "requires-rabbitmq")]
public sealed class ConsumerRoutingKeysSampleSmokeTests(SamplesAppFixture fixture)
    : IClassFixture<SamplesAppFixture>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task SmokeTest_ConsumerRoutingKeys_AllThreeBehaviors()
    {
        // ── Arrange ─────────────────────────────────────────────────────────────
        // "consumer-routing-keys" is the logical resource name registered in AppHost.
        using HttpClient client = fixture.CreateHttpClient("consumer-routing-keys");

        // Generous CTS: AppHost boots RabbitMQ + all samples; POST /run waits up to 30 s
        // internally for consumers to record their observations. 60 s outer bound matches
        // the CompetingResponders smoke test pattern.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // ── Act: POST /run ───────────────────────────────────────────────────────
        HttpResponseMessage httpResponse =
            await client.PostAsync("/run", content: null, cts.Token);

        // ── Assert: HTTP 200 ─────────────────────────────────────────────────────
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "POST /run must return 200 OK when all three consumers have recorded observations");

        // ── Assert: response body ────────────────────────────────────────────────
        string body = await httpResponse.Content.ReadAsStringAsync(cts.Token);
        body.Should().NotBeNullOrEmpty("response body must be non-empty");

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(body, Json);

        JsonElement[] observations = [.. json.GetProperty("observations").EnumerateArray()];
        observations.Should().HaveCount(3,
            "exactly 3 observations expected — one per published delivery");

        // Index by routing key for deterministic assertions independent of arrival order.
        Dictionary<string, JsonElement> byRoutingKey = observations.ToDictionary(
            o => o.GetProperty("routingKey").GetString()!,
            o => o,
            StringComparer.Ordinal);

        // ── Assert 1: most-specific-wins ────────────────────────────────────────
        // "transfer.eu.priority" matches both "transfer.eu.*" (Region) and
        // "transfer.eu.priority" (Priority, exact). The exact pattern wins — most-specific-wins.
        byRoutingKey.Should().ContainKey("transfer.eu.priority",
            "delivery on transfer.eu.priority must be observed");

        JsonElement priority = byRoutingKey["transfer.eu.priority"];
        priority.GetProperty("consumer").GetString().Should().Be("PriorityTransferConsumer",
            "exact routing-key pattern wins over wildcard — most-specific-wins dispatch");
        priority.GetProperty("typeLess").GetBoolean().Should().BeFalse(
            "typed delivery: BW-MessageType header was present so dispatch used the typed path");

        // ── Assert 2: multi-consumer routing ────────────────────────────────────
        // "transfer.eu.standard" matches only "transfer.eu.*" — RegionTransferConsumer.
        byRoutingKey.Should().ContainKey("transfer.eu.standard",
            "delivery on transfer.eu.standard must be observed");

        JsonElement standard = byRoutingKey["transfer.eu.standard"];
        standard.GetProperty("consumer").GetString().Should().Be("RegionTransferConsumer",
            "wildcard pattern 'transfer.eu.*' handles standard EU transfers");
        standard.GetProperty("typeLess").GetBoolean().Should().BeFalse(
            "typed delivery: BW-MessageType header was present so dispatch used the typed path");

        // ── Assert 3: type-less interop ─────────────────────────────────────────
        // "legacy.audit.created" has no BW-MessageType header — type-less dispatch.
        // LegacyNotificationConsumer is the only AcceptUntyped consumer matching "legacy.#".
        byRoutingKey.Should().ContainKey("legacy.audit.created",
            "delivery on legacy.audit.created must be observed");

        JsonElement legacy = byRoutingKey["legacy.audit.created"];
        legacy.GetProperty("consumer").GetString().Should().Be("LegacyNotificationConsumer",
            "AcceptUntyped consumer with pattern 'legacy.#' handles type-less deliveries");
        legacy.GetProperty("typeLess").GetBoolean().Should().BeTrue(
            "delivery had no BW-MessageType header — dispatched via the type-less path");

        // Non-vacuous echo: echo must start with "audit-event-for-" to prove the raw-first
        // deserialization into LegacyNotification succeeded and the Detail field was correctly read.
        string? legacyEcho = legacy.GetProperty("echo").GetString();
        legacyEcho.Should().NotBeNullOrEmpty(
            "echo must be non-empty — proves raw-first deserialization into LegacyNotification succeeded");
        legacyEcho.Should().StartWith("audit-event-for-",
            "echo must match the Detail field of the published LegacyNotification");
    }
}
