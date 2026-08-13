using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace BareWire.E2ETests.ConsumerDefinitionShowcase;

/// <summary>
/// Smoke E2E test for the BareWire.Samples.ConsumerDefinitionShowcase sample.
/// Boots the full AppHost (RabbitMQ + all samples) via <see cref="SamplesAppFixture"/>, then
/// exercises POST /run and asserts that all three configuration axes worked together:
/// <list type="bullet">
///   <item>Routing: the delivery on <c>transfer.eu.priority</c> was dispatched to
///         <c>TransferConsumer</c> — proving the routing-key patterns declared on
///         <c>TransferConsumerDefinition</c> (discovered via DI, not inline) were applied.</item>
///   <item>Retry: the recorded observation carries an attempt count greater than 1 — proving the
///         retry policy declared on the same definition actually re-delivered the message after
///         the consumer's simulated transient failures.</item>
///   <item>Topology: the delivery reached the consumer at all (HTTP 200 with exactly one
///         observation) — proving the exchange/queue/binding declared opt-in via
///         <c>DeclareTopology</c> were created at bus start-up.</item>
/// </list>
/// </summary>
[Trait("Category", "requires-rabbitmq")]
public sealed class ConsumerDefinitionShowcaseSampleSmokeTests(SamplesAppFixture fixture)
    : IClassFixture<SamplesAppFixture>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task SmokeTest_ConsumerDefinitionShowcase_RoutingRetryAndTopology()
    {
        // ── Arrange ─────────────────────────────────────────────────────────────
        // "consumer-definition-showcase" is the logical resource name registered in AppHost.
        using HttpClient client = fixture.CreateHttpClient("consumer-definition-showcase");

        // Generous CTS: AppHost boots RabbitMQ + all samples; POST /run waits up to 30 s
        // internally for the consumer to record its observation (proving the retry policy fired).
        // 60 s outer bound matches the ConsumerRoutingKeys smoke test pattern.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // ── Act: POST /run ───────────────────────────────────────────────────────
        HttpResponseMessage httpResponse =
            await client.PostAsync("/run", content: null, cts.Token);

        // ── Assert: HTTP 200 ─────────────────────────────────────────────────────
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "POST /run must return 200 OK once TransferConsumer has recorded its observation — " +
            "proving the DeclareTopology-created exchange/queue/binding delivered the message");

        // ── Assert: response body ────────────────────────────────────────────────
        string body = await httpResponse.Content.ReadAsStringAsync(cts.Token);
        body.Should().NotBeNullOrEmpty("response body must be non-empty");

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(body, Json);

        JsonElement[] observations = [.. json.GetProperty("observations").EnumerateArray()];
        observations.Should().ContainSingle(
            "exactly one observation expected — one published delivery, dispatched once it succeeded");

        JsonElement observation = observations[0];

        // ── Assert 1: routing ────────────────────────────────────────────────────
        // The definition's routing-key patterns ("transfer.eu.*" and the exact "transfer.eu.priority")
        // were applied by DI discovery, not declared inline at the endpoint.
        observation.GetProperty("consumer").GetString().Should().Be("TransferConsumer",
            "the delivery must be dispatched to TransferConsumer — proving the routing-key patterns " +
            "declared on TransferConsumerDefinition (discovered via DI) were merged into the endpoint");

        observation.GetProperty("routingKey").GetString().Should().Be("transfer.eu.priority",
            "the published delivery used routing key transfer.eu.priority");

        // ── Assert 2: retry ──────────────────────────────────────────────────────
        // TransferConsumer deliberately fails its first attempts; a recorded attempt count above 1
        // is direct proof that the definition's Retry(r => r.Exponential(...)) policy re-delivered
        // the message rather than the consumer succeeding on the first try.
        observation.GetProperty("attempts").GetInt32().Should().BeGreaterThan(1,
            "the consumer only succeeds after simulated transient failures — an attempt count above " +
            "1 proves the retry policy declared on TransferConsumerDefinition actually fired");

        // ── Assert 3: topology (implicit) ────────────────────────────────────────
        // A non-empty, single observation already proves the delivery reached the consumer — the
        // exchange, queue, and binding declared opt-in via DeclareTopology were created at bus
        // start-up and TransferPublisher's matching exchange declaration did not collide with them.
        observation.GetProperty("transferId").GetString().Should().NotBeNullOrEmpty(
            "the observation must carry the transfer id of the delivered message");
    }
}
