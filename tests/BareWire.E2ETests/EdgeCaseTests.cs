using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.E2ETests.Helpers;
using Xunit;

namespace BareWire.E2ETests;

/// <summary>
/// E2E edge-case scenarios — partitioning, backpressure, outbox pending state.
/// </summary>
public sealed class EdgeCaseTests(SamplesAppFixture fixture) : IClassFixture<SamplesAppFixture>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);

    // ── E2E-012: MultiConsumerPartitioning ──────────────────────────────────────

    [Fact]
    public async Task E2E012_MultiConsumerPartitioning_SequentialPerCorrelationId()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("multi-consumer-partitioning");

        // Act — generate 1000 events distributed across 10 CorrelationIds
        var generateResponse = await client.PostAsync("/events/generate", null);
        generateResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Wait for all events to be processed (1000 events at 16 concurrent consumers)
        var log = await client.PollUntilAsync<JsonElement[]>(
            "/events/processing-log",
            items => items.Length >= 1000,
            PollTimeout,
            pollInterval: TimeSpan.FromSeconds(2));

        // Assert — verify sequential processing per CorrelationId
        // Group entries by CorrelationId and check ProcessedAt ordering
        var groups = log
            .GroupBy(e => e.GetProperty("correlationId").GetString())
            .ToList();

        groups.Should().NotBeEmpty("should have multiple correlation groups");

        foreach (var group in groups)
        {
            var timestamps = group
                .Select(e => e.GetProperty("processedAt").GetDateTime())
                .ToList();

            // Within each CorrelationId, processing should be sequential (timestamps are ordered)
            timestamps.Should().BeInAscendingOrder(
                $"events for CorrelationId '{group.Key}' must be processed sequentially");
        }
    }

    // ── E2E-013: BackpressureDemo ───────────────────────────────────────────────

    [Fact]
    public async Task E2E013_BackpressureDemo_HighRate_MetricsAvailable()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("backpressure-demo");

        // Act — start load test at 5000 msg/s
        var startResponse = await client.PostAsync("/load-test/start?rate=5000", null);
        startResponse.EnsureSuccessStatusCode();

        // Let it run for a few seconds
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Get metrics
        var metricsResponse = await client.GetAsync("/metrics");
        metricsResponse.EnsureSuccessStatusCode();
        var metrics = await metricsResponse.Content.ReadFromJsonAsync<JsonElement>(Json);

        // Stop load test
        var stopResponse = await client.PostAsync("/load-test/stop", null);
        stopResponse.EnsureSuccessStatusCode();

        // Assert — metrics should show activity
        metrics.GetProperty("isRunning").GetBoolean().Should().BeTrue();
        metrics.GetProperty("totalPublished").GetInt64().Should().BeGreaterThan(0,
            "load generator should have published messages");
    }

    // ── E2E-014: TransactionalOutbox pending ────────────────────────────────────

    [Fact]
    public async Task E2E014_TransactionalOutbox_PendingEndpointResponds()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("transactional-outbox");

        // Act — create a transfer
        var transferResponse = await client.PostAsJsonAsync("/transfers", new
        {
            FromAccount = "ACC-X01",
            ToAccount = "ACC-X02",
            Amount = 25.00m,
        });
        transferResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Immediately check pending outbox
        var pendingResponse = await client.GetAsync("/outbox/pending");
        pendingResponse.EnsureSuccessStatusCode();

        var pending = await pendingResponse.Content.ReadFromJsonAsync<JsonElement>(Json);

        // Assert — the pending endpoint should return a valid response (pendingCount >= 0)
        pending.TryGetProperty("pendingCount", out var count).Should().BeTrue(
            "outbox pending endpoint should return a pendingCount property");
        count.GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }
}
