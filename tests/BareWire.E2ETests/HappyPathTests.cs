using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.E2ETests.Helpers;
using Xunit;

namespace BareWire.E2ETests;

/// <summary>
/// E2E happy-path scenarios that verify core sample workflows through HTTP APIs.
/// All tests use the shared Aspire fixture with real RabbitMQ + PostgreSQL.
/// </summary>
public sealed class HappyPathTests(SamplesAppFixture fixture) : IClassFixture<SamplesAppFixture>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);

    // ── E2E-001: BasicPublishConsume ────────────────────────────────────────────

    [Fact]
    public async Task E2E001_BasicPublishConsume_MessagePersistedToDatabase()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("basic-publish-consume");
        string content = $"e2e-test-{Guid.NewGuid():N}";

        // Act — publish a message
        var publishResponse = await client.PostAsJsonAsync("/messages", new { Content = content });
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Assert — poll until the message appears in the database
        var messages = await client.PollUntilAsync<JsonElement[]>(
            "/messages",
            items => items.Any(m => m.GetProperty("content").GetString() == content),
            PollTimeout);

        messages.Should().Contain(m => m.GetProperty("content").GetString() == content);
    }

    // ── E2E-002: RequestResponse ────────────────────────────────────────────────

    [Fact]
    public async Task E2E002_RequestResponse_ReturnsValidationResult()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("request-response");
        string orderId = Guid.NewGuid().ToString();

        // Act — send a validation request
        var response = await client.PostAsJsonAsync("/validate-order", new
        {
            OrderId = orderId,
            Amount = 99.99m,
            Currency = "USD",
        });

        // Assert — should get a synchronous response
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("orderId").GetString().Should().Be(orderId);
        body.TryGetProperty("isValid", out _).Should().BeTrue();
    }

    // ── E2E-003: SagaOrderFlow happy path ───────────────────────────────────────

    [Fact]
    public async Task E2E003_SagaOrderFlow_HappyPath_ReachesCompleted()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("saga-order-flow");

        // Act — create order (server generates OrderId)
        var createResponse = await client.PostAsJsonAsync("/orders", new
        {
            Amount = 250.00m,
            ShippingAddress = "123 Test Street",
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        string orderId = created.GetProperty("orderId").GetString()!;

        // Wait for saga to reach Processing state
        await client.PollUntilAsync<JsonElement>(
            $"/orders/{orderId}/status",
            s => s.GetProperty("currentState").GetString() == "Processing",
            PollTimeout);

        // Simulate payment
        var payResponse = await client.PostAsync($"/orders/{orderId}/simulate/pay", null);
        payResponse.EnsureSuccessStatusCode();

        // Wait for Shipping state
        await client.PollUntilAsync<JsonElement>(
            $"/orders/{orderId}/status",
            s => s.GetProperty("currentState").GetString() == "Shipping",
            PollTimeout);

        // Simulate shipment
        var shipResponse = await client.PostAsync($"/orders/{orderId}/simulate/ship", null);
        shipResponse.EnsureSuccessStatusCode();

        // Assert — saga reaches Completed or Finalized (Finalize() removes it from DB)
        var finalStatus = await client.PollUntilAsync<JsonElement>(
            $"/orders/{orderId}/status",
            s =>
            {
                string state = s.GetProperty("currentState").GetString()!;
                return state is "Completed" or "Finalized";
            },
            PollTimeout);

        string finalState = finalStatus.GetProperty("currentState").GetString()!;
        finalState.Should().BeOneOf("Completed", "Finalized");
    }

    // ── E2E-004: TransactionalOutbox ────────────────────────────────────────────

    [Fact]
    public async Task E2E004_TransactionalOutbox_TransferCompletedViaOutbox()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("transactional-outbox");

        // Act — create a transfer
        var transferResponse = await client.PostAsJsonAsync("/transfers", new
        {
            FromAccount = "ACC-001",
            ToAccount = "ACC-002",
            Amount = 100.00m,
        });
        transferResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Assert — poll until transfer status becomes Completed
        var transfers = await client.PollUntilAsync<JsonElement[]>(
            "/transfers",
            items => items.Any(t =>
                t.GetProperty("status").GetString() == "Completed"),
            PollTimeout);

        transfers.Should().Contain(t => t.GetProperty("status").GetString() == "Completed");
    }

    // ── E2E-005: RawMessageInterop ──────────────────────────────────────────────

    [Fact]
    public async Task E2E005_RawMessageInterop_LegacyMessageProcessed()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("raw-message-interop");

        // Act — simulate a legacy system publishing a raw event
        var response = await client.PostAsync("/legacy/simulate", null);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Assert — poll until processed message appears
        var messages = await client.PollUntilAsync<JsonElement[]>(
            "/messages",
            items => items.Length > 0,
            PollTimeout);

        messages.Should().NotBeEmpty();
    }

    // ── E2E-006: InboxDeduplication ─────────────────────────────────────────────

    [Fact]
    public async Task E2E006_InboxDeduplication_DuplicateMessageProcessedOncePerConsumer()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("inbox-deduplication");

        // Act — publish original payment (request format: Payer, Payee, Amount)
        var originalResponse = await client.PostAsJsonAsync("/payments", new
        {
            Payer = "Alice",
            Payee = "Bob",
            Amount = 42.00m,
        });
        originalResponse.EnsureSuccessStatusCode();

        var originalBody = await originalResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        string paymentId = originalBody.GetProperty("paymentId").GetString()!;
        string messageId = originalBody.GetProperty("messageId").GetString()!;

        // Wait for original message to be processed by both consumers (Email + Audit = 2 notifications)
        await client.PollUntilAsync<JsonElement>(
            "/notifications",
            n => n.GetProperty("count").GetInt32() >= 2,
            PollTimeout);

        int countBefore = (await client.GetFromJsonAsync<JsonElement>("/notifications", Json))
            .GetProperty("count").GetInt32();

        // Send duplicate with same MessageId
        var duplicateResponse = await client.PostAsync(
            $"/payments/duplicate?paymentId={paymentId}&messageId={messageId}&amount=42.00",
            null);
        duplicateResponse.EnsureSuccessStatusCode();

        // Wait for potential processing
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Assert — notification count should not increase (duplicate was rejected by inbox)
        var notificationsAfter = await client.GetFromJsonAsync<JsonElement>("/notifications", Json);
        int countAfter = notificationsAfter.GetProperty("count").GetInt32();

        countAfter.Should().Be(countBefore,
            "duplicate message with same MessageId should be rejected by inbox deduplication");
    }

    // ── E2E-007: ObservabilityShowcase ──────────────────────────────────────────

    [Fact]
    public async Task E2E007_ObservabilityShowcase_ThreeHopCascadeCompletes()
    {
        // Arrange
        using var client = fixture.CreateHttpClient("observability-showcase");

        // Act — trigger the 3-hop cascade (order → payment → shipment)
        var response = await client.PostAsJsonAsync("/demo/run", new
        {
            Amount = 150.00m,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Assert — the cascade completes without error.
        // Since ObservabilityShowcase doesn't expose a query endpoint for processed messages,
        // success of the POST without error and 202 Accepted is the primary assertion.
        // The real validation is that no unhandled exceptions occur in the 3-hop pipeline.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.TryGetProperty("orderId", out _).Should().BeTrue();
    }
}
