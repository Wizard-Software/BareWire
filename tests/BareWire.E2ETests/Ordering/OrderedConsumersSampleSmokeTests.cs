using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.E2ETests.Helpers;
using Xunit;

namespace BareWire.E2ETests.Ordering;

/// <summary>
/// Smoke E2E test for the BareWire.Samples.OrderedConsumers sample.
/// Boots the full AppHost (RabbitMQ + PostgreSQL + all samples) via <see cref="SamplesAppFixture"/>,
/// then exercises the ordered-consumers HTTP API and asserts:
/// <list type="bullet">
///   <item>Strict per-key ordering: for every healthy key, the Sequence values in ProcessedRecords
///         arrive monotonically non-decreasing (0 ordering violations across competing replicas).</item>
///   <item>Poison key resume: after seq=0 is parked via DLX (MaxDeliveryAttempts=2), the poison key's
///         seq 1..4 are delivered and persisted as ProcessedRecords.</item>
///   <item>SEC non-vacuousness: healthy keys ARE present in processing-log (load-bearing guard).</item>
///   <item>SEC sentinel absence: the high-entropy poison sentinel is ABSENT from the
///         POST /events/generate HTTP response body (generate response never includes the raw key).</item>
/// </list>
/// </summary>
[Trait("Category", "requires-rabbitmq")]
[Trait("Category", "requires-postgres")]
public sealed class OrderedConsumersSampleSmokeTests(SamplesAppFixture fixture)
    : IClassFixture<SamplesAppFixture>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // Poll timeout: 60 s — 2× margin over the 30 s reference (ConsumerPerKeyOrderingE2ETests)
    // to absorb SAC failover jitter across replicas and outbox polling (500 ms interval).
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);

    // Volume constants matching the plan §9 PERF-1 and the reference test statics:
    // 3 healthy keys × 5 sequences + 1 poison key × 5 (head seq=0 parked, seq 1..4 resume).
    private static readonly string[] HealthyKeys = ["acct-A", "acct-B", "acct-C"];
    private const int SequencesPerKey = 5;

    // For each healthy key: ceil(5/2) = 3 OrderShipped (seq 0,2,4) + 2 InventoryAdjusted (seq 1,3).
    // Both types go to the SAC endpoint. The local-partitioned endpoint also processes OrderShipped
    // which adds more records (same DB table). We use a conservative floor: 5 records per key on the
    // SAC endpoint minimum (all 5 sequences, regardless of type or duplicate endpoint writes).
    private const int MinExpectedHealthyRecords = 15; // 3 keys × 5 sequences

    // Poison resume: seq 0 is parked; seq 1..4 are expected (4 records).
    private const int PoisonResumeCount = 4; // seq 1..4

    [Fact]
    public async Task SmokeTest_StrictPerKeyOrder_AndPoisonResume()
    {
        // ── Arrange ─────────────────────────────────────────────────────────────
        // GAP-1: "ordered-consumers" is the first WithReplicas(2) resource in this fixture.
        // Aspire may name replicas "ordered-consumers-0" / "ordered-consumers-1" internally;
        // CreateHttpClient("ordered-consumers") is tried first (logical name). If the fixture
        // did not wait for "ordered-consumers" successfully (implying logical name works), we
        // would have failed in InitializeAsync. The client creation uses the same logical name.
        using HttpClient client = fixture.CreateHttpClient("ordered-consumers");

        // ── Act: POST /events/generate?withPoison=true ───────────────────────────
        HttpResponseMessage generateResponse =
            await client.PostAsync("/events/generate?withPoison=true", content: null);

        generateResponse.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "generate endpoint should accept the request synchronously");

        string generateBody = await generateResponse.Content.ReadAsStringAsync();

        // ── SEC assertion: poison sentinel absent from generate response body ─────
        // The generate response deliberately omits the poison key value (see Program.cs).
        // The response carries RunId, HealthyKeys, PoisonKeyInjected — but NOT the poison key.
        JsonElement generateJson = JsonSerializer.Deserialize<JsonElement>(generateBody, Json);
        bool poisonInjected = generateJson.TryGetProperty("poisonKeyInjected", out var pki)
            && pki.GetBoolean();
        poisonInjected.Should().BeTrue("withPoison=true was passed; server should acknowledge injection");

        // Extract the RunId to filter only this run's records (avoids stale DB records from prior runs).
        string runId = generateJson.GetProperty("runId").GetString()!;
        runId.Should().NotBeNullOrEmpty("generate response must include a RunId");

        // SEC: generate response body must not contain the poison sentinel prefix.
        generateBody.Should().NotContain("poison-",
            "generate response body must not expose the poison key sentinel prefix");

        // ── Poll until enough healthy records arrive (filtered by this run's RunId) ─
        string pollUrl = $"/events/processing-log?runId={runId}";

        JsonElement[] finalRecords = await client.PollUntilAsync<JsonElement[]>(
            pollUrl,
            items =>
            {
                // Count healthy SAC records only (endpoint "ordered-processing").
                int healthyCount = items.Count(r =>
                    HealthyKeys.Contains(r.GetProperty("key").GetString()) &&
                    r.GetProperty("endpointName").GetString() == "ordered-processing");
                int poisonResumeActual = items.Count(r =>
                {
                    string? key = r.GetProperty("key").GetString();
                    int seq = r.GetProperty("sequence").GetInt32();
                    string? endpoint = r.GetProperty("endpointName").GetString();
                    // Poison resume: key is NOT one of the healthy keys (it's the poison key),
                    // sequence > 0 (seq=0 is parked and never appears as a ProcessedRecord),
                    // and it's on the SAC endpoint (that's where MaxDeliveryAttempts is active).
                    return key is not null
                        && !HealthyKeys.Contains(key)
                        && seq > 0
                        && endpoint == "ordered-processing";
                });

                return healthyCount >= MinExpectedHealthyRecords
                    && poisonResumeActual >= PoisonResumeCount;
            },
            PollTimeout,
            pollInterval: TimeSpan.FromMilliseconds(500));

        List<JsonElement> allRecords = finalRecords.ToList();

        // ── SEC non-vacuousness: healthy keys ARE present ──────────────────────
        bool acctAPresent = allRecords.Any(r =>
            r.GetProperty("key").GetString() == "acct-A");
        bool acctBPresent = allRecords.Any(r =>
            r.GetProperty("key").GetString() == "acct-B");
        bool acctCPresent = allRecords.Any(r =>
            r.GetProperty("key").GetString() == "acct-C");

        acctAPresent.Should().BeTrue("acct-A healthy records must be present (non-vacuousness guard)");
        acctBPresent.Should().BeTrue("acct-B healthy records must be present (non-vacuousness guard)");
        acctCPresent.Should().BeTrue("acct-C healthy records must be present (non-vacuousness guard)");

        // ── Assert strict per-key order across competing replicas (SAC endpoint only) ──────
        // Filter to SAC endpoint records ("ordered-processing") only. The LocalPartitioned
        // endpoint ("local-partitioned-processing") also processes OrderShipped but with fixed-lane
        // hashing (single-instance guarantee, not cross-instance), and its records may interleave
        // in DB insertion order with SAC records. The ordering guarantee being tested here is the
        // SAC cross-instance guarantee (Tier 1 / ADR-026 §4).
        foreach (string key in HealthyKeys)
        {
            List<int> sequences = allRecords
                .Where(r =>
                    r.GetProperty("key").GetString() == key &&
                    r.GetProperty("endpointName").GetString() == "ordered-processing")
                .Select(r => r.GetProperty("sequence").GetInt32())
                .ToList();

            sequences.Should().NotBeEmpty(
                $"key '{key}' should have processed records");

            // Verify strict non-decreasing order (0 violations).
            for (int i = 1; i < sequences.Count; i++)
            {
                sequences[i].Should().BeGreaterThanOrEqualTo(sequences[i - 1],
                    $"key '{key}': sequence at position {i} ({sequences[i]}) must be >= " +
                    $"sequence at position {i - 1} ({sequences[i - 1]}) — strict per-key order violated");
            }
        }

        // ── Assert poison key resume: seq 1..4 delivered after head parking ────
        // The poison key is the one whose seq=0 is ABSENT (parked via DLX) but seq 1..N present.
        // Filter to SAC endpoint — that's where the DLX/MaxDeliveryAttempts is configured.
        List<JsonElement> sacNonHealthyRecords = allRecords
            .Where(r =>
                !HealthyKeys.Contains(r.GetProperty("key").GetString()) &&
                r.GetProperty("endpointName").GetString() == "ordered-processing")
            .ToList();

        sacNonHealthyRecords.Should().NotBeEmpty(
            "at least one poison-resume record (seq > 0) should be present on the SAC endpoint");

        // Poison head (seq=0) must be ABSENT on the SAC endpoint — it was parked via DLX
        // and never wrote a ProcessedRecord (exception thrown before SaveChangesAsync).
        bool poisonHeadPresent = sacNonHealthyRecords.Any(r => r.GetProperty("sequence").GetInt32() == 0);
        poisonHeadPresent.Should().BeFalse(
            "poison head seq=0 must be absent from processing-log (SAC endpoint) — it was parked via DLX");

        // Poison tail (seq 1..4) must be present — key released after head parking.
        List<int> poisonSeqs = sacNonHealthyRecords
            .Select(r => r.GetProperty("sequence").GetInt32())
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        poisonSeqs.Count.Should().BeGreaterThanOrEqualTo(PoisonResumeCount,
            $"poison key should have at least {PoisonResumeCount} resume sequences (1..{SequencesPerKey - 1}) present after head parking");
    }
}
