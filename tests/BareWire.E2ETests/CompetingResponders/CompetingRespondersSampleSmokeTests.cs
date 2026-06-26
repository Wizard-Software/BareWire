using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace BareWire.E2ETests.CompetingResponders;

/// <summary>
/// Smoke E2E test for the BareWire.Samples.CompetingResponders sample.
/// Boots the full AppHost (RabbitMQ + all samples) via <see cref="SamplesAppFixture"/>,
/// then exercises the competing-responders HTTP API and asserts:
/// <list type="bullet">
///   <item>First-in-wins: a single <c>POST /ask</c> returns exactly one response (contract
///         of <c>GetResponseAsync</c>) — load-bearing assertion.</item>
///   <item>Non-vacuousness: <c>echo</c> matches the sent payload (fails if correlation
///         or echo is broken) and <c>responderId</c> is non-null/non-empty (proves a real
///         responder answered, not an empty default).</item>
/// </list>
/// </summary>
[Trait("Category", "requires-rabbitmq")]
public sealed class CompetingRespondersSampleSmokeTests(SamplesAppFixture fixture)
    : IClassFixture<SamplesAppFixture>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task SmokeTest_CompetingResponders_FirstInWins()
    {
        // ── Arrange ─────────────────────────────────────────────────────────────
        // "competing-responders" is the logical resource name for the replica set
        // (WithReplicas(2)). Aspire resolves it to any healthy replica; CreateHttpClient
        // uses the same logical name that WaitForResourceAsync waited for.
        using HttpClient client = fixture.CreateHttpClient("competing-responders");

        // Use a generous timeout to absorb replica startup and broker ready-wait.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        const string payload = "ping-1";

        // ── Act: POST /ask?payload=ping-1 ────────────────────────────────────────
        // GetResponseAsync is synchronous from the caller's perspective — it waits
        // until a responder calls RespondAsync. No polling needed; assert the response body.
        HttpResponseMessage httpResponse =
            await client.PostAsync($"/ask?payload={payload}", content: null, cts.Token);

        // ── Assert: HTTP 200 ──────────────────────────────────────────────────────
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the /ask endpoint must return 200 OK when a responder answers");

        // ── Assert: response body ────────────────────────────────────────────────
        string body = await httpResponse.Content.ReadAsStringAsync(cts.Token);
        body.Should().NotBeNullOrEmpty("response body must be non-empty");

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(body, Json);

        // Non-vacuousness: echo must match the sent payload.
        // This assertion fails if the correlation path or echo field is broken.
        string? echo = json.GetProperty("echo").GetString();
        echo.Should().Be(payload,
            "echo must match the sent payload — fails if correlation or echo is broken");

        // Non-vacuousness: responderId must be non-null/non-empty.
        // Proves a real responder answered, not an empty default.
        string? responderId = json.GetProperty("responderId").GetString();
        responderId.Should().NotBeNullOrEmpty(
            "responderId must be present and non-empty — proves a real responder instance answered");
    }
}
