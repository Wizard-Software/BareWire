using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AwesomeAssertions;

using Xunit;

namespace BareWire.E2ETests.InMemoryModularMonolith;

/// <summary>
/// End-to-end test for the <c>BareWire.Samples.InMemoryModularMonolith</c> sample running on the
/// RabbitMQ transport via the Aspire AppHost. Proves that switching <c>Transport</c> from
/// <c>InMemory</c> to <c>RabbitMQ</c> — the only registration change, per
/// <c>Messaging/TransportRegistration.cs</c> — still fans <c>OrderPlaced</c> out to both modules and
/// routes <c>PaymentCaptured</c> from Billing to Shipping through the topic exchange.
/// </summary>
[Trait("Category", "requires-rabbitmq")]
public sealed class InMemoryModularMonolithRabbitMqSmokeTests(SamplesAppFixture fixture)
    : IClassFixture<SamplesAppFixture>
{
    private static readonly TimeSpan HealthPollTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadyToShipPollTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task PlaceOrder_RabbitMqTransport_OrderPlacedFansOutAndPaymentCapturedReachesShipping()
    {
        using HttpClient client = fixture.CreateHttpClient("inmemory-modular-monolith");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // The resource is "Running" once the fixture's own wait completes, but the app may not be
        // listening for HTTP yet — poll /health before the first POST. /health returns a plain-text
        // status word (no custom ResponseWriter is configured), not JSON, so the shared
        // HttpClientExtensions.PollUntilAsync<T> (which deserializes JSON) does not fit here.
        await WaitForHealthyAsync(client, HealthPollTimeout, cts.Token);

        HttpResponseMessage placed = await client.PostAsJsonAsync(
            "/orders", new { customerId = "e2e-customer", amount = 42.50m }, cts.Token);
        placed.StatusCode.Should().Be(HttpStatusCode.Accepted);

        string orderId = (await placed.Content.ReadFromJsonAsync<JsonElement>(cts.Token))
            .GetProperty("orderId").GetString()!;

        // GET /orders/{orderId} returns 404 until at least one module consumer has recorded the
        // order — the POST above only enqueues the OrderPlaced fan-out, it does not wait for
        // consumers to process it. The shared HttpClientExtensions.PollUntilAsync<T> calls
        // EnsureSuccessStatusCode() on every response, which makes that race fatal, so this poll
        // uses a local helper that treats 404 as "not yet" instead.
        JsonElement status = await PollUntilReadyToShipAsync(client, orderId, ReadyToShipPollTimeout, cts.Token);

        status.GetProperty("transport").GetString().Should().Be("RabbitMQ");
        status.GetProperty("billingCaptured").GetBoolean().Should().BeTrue();
        status.GetProperty("shippingStatus").GetString().Should().Be("ReadyToShip");
    }

    /// <summary>
    /// Polls <c>GET /health</c> until it returns 200, retrying on both a non-success status and a
    /// connection failure (the app may not be listening yet even though Aspire reports the resource
    /// as "Running").
    /// </summary>
    private static async Task WaitForHealthyAsync(
        HttpClient client, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        while (true)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync("/health", cts.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The app may not be accepting connections yet — retry until the timeout.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
        }
    }

    /// <summary>
    /// Polls <c>GET /orders/{orderId}</c> until the response reports
    /// <c>shippingStatus == "ReadyToShip"</c>. A <c>404 Not Found</c> means no module consumer has
    /// recorded the order yet (the initial <c>POST /orders</c> only enqueues the fan-out) and is
    /// treated as "not yet ready" rather than a fatal error; any other non-success status still
    /// fails fast via <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>.
    /// </summary>
    private static async Task<JsonElement> PollUntilReadyToShipAsync(
        HttpClient client, string orderId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        string lastStatus = "(no response received yet)";
        string lastBody = "(no response received yet)";

        try
        {
            while (true)
            {
                using HttpResponseMessage response = await client.GetAsync($"/orders/{orderId}", cts.Token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    lastStatus = "404 Not Found";
                    lastBody = "(order not yet recorded by any module)";
                }
                else
                {
                    response.EnsureSuccessStatusCode();

                    lastBody = await response.Content.ReadAsStringAsync(cts.Token);
                    JsonElement element = JsonSerializer.Deserialize<JsonElement>(lastBody);
                    lastStatus = element.TryGetProperty("shippingStatus", out JsonElement s)
                        ? s.GetString() ?? "(null)"
                        : "(missing shippingStatus)";

                    if (lastStatus == "ReadyToShip")
                    {
                        return element;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Polling /orders/{orderId} did not reach shippingStatus \"ReadyToShip\" within {timeout}. "
                    + $"Last observed status: {lastStatus}, body: {lastBody}");
        }
    }
}
