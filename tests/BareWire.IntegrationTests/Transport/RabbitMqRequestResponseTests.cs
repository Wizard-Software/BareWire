using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Integration tests for the request-response pattern via <see cref="RabbitMqRequestClient{TRequest}"/>.
/// All tests require a running RabbitMQ instance provisioned via <see cref="AspireFixture"/>.
/// </summary>
/// <remarks>
/// These tests are stubs pending the full E2E infrastructure from task 3.10.
/// Each test is marked with the "Integration" category trait.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class RabbitMqRequestResponseTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // Stored for use in full E2E implementations (task 3.10).
    private readonly AspireFixture _fixture = fixture;

    // ── Test records ───────────────────────────────────────────────────────────

    private sealed record PingRequest(string Payload);
    private sealed record PingResponse(string Echo);

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_HappyPath_ReturnsTypedResponse()
    {
        // TODO: requires running RabbitMQ (task 3.10)
        // Scenario:
        //   1. Declare a request queue via RabbitMqTopologyConfigurator.
        //   2. Start a consumer on that queue; on receipt, call ConsumeContext.RespondAsync().
        //   3. Create a RabbitMqRequestClient<PingRequest> via the adapter.
        //   4. Call GetResponseAsync<PingResponse>(new PingRequest("hello")).
        //   5. Assert: Response.Message.Echo == "hello".
        await Task.CompletedTask;
        Assert.Fail("Not yet implemented — requires RabbitMQ container (task 3.10).");
    }

    [Fact]
    public async Task GetResponseAsync_Timeout_ThrowsRequestTimeoutException()
    {
        // TODO: requires running RabbitMQ (task 3.10)
        // Scenario:
        //   1. Declare a request queue but do NOT start any consumer (no responder).
        //   2. Create a RabbitMqRequestClient<PingRequest> with a short timeout (e.g. 500ms).
        //   3. Call GetResponseAsync<PingResponse>().
        //   4. Assert: throws RequestTimeoutException with Timeout == configured timeout.
        await Task.CompletedTask;
        Assert.Fail("Not yet implemented — requires RabbitMQ container (task 3.10).");
    }

    [Fact]
    public async Task GetResponseAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        // TODO: requires running RabbitMQ (task 3.10)
        // Scenario:
        //   1. Declare a request queue but do NOT start any consumer.
        //   2. Create a CancellationTokenSource and cancel it immediately.
        //   3. Call GetResponseAsync<PingResponse>(request, cts.Token).
        //   4. Assert: throws OperationCanceledException (not RequestTimeoutException).
        await Task.CompletedTask;
        Assert.Fail("Not yet implemented — requires RabbitMQ container (task 3.10).");
    }

    [Fact]
    public async Task GetResponseAsync_MultipleRequests_RoutedByCorrelationId()
    {
        // TODO: requires running RabbitMQ (task 3.10)
        // Scenario:
        //   1. Declare a request queue with a consumer that echoes the payload back.
        //   2. Fire 3 concurrent GetResponseAsync calls with distinct payloads.
        //   3. Assert: each caller receives the response matching its own request by correlationId.
        await Task.CompletedTask;
        Assert.Fail("Not yet implemented — requires RabbitMQ container (task 3.10).");
    }

    [Fact]
    public async Task Dispose_CancelsPendingRequests()
    {
        // TODO: requires running RabbitMQ (task 3.10)
        // Scenario:
        //   1. Declare a request queue with no consumer.
        //   2. Start a GetResponseAsync call (long timeout).
        //   3. Immediately call DisposeAsync on the client.
        //   4. Assert: the pending GetResponseAsync task completes with OperationCanceledException.
        await Task.CompletedTask;
        Assert.Fail("Not yet implemented — requires RabbitMQ container (task 3.10).");
    }
}
