// BareWire.Samples.ConsumerDefinitionShowcase — demonstrates ConsumerDefinition<TConsumer> discovered
// via explicit DI registration, co-locating a retry policy and routing-key patterns next to the
// consumer, plus the opt-in transport-level DeclareTopology helper applied at endpoint registration.
//
// What this sample shows:
//   1. Retry policy inside the definition — TransferConsumerDefinition.Configure calls
//      consumer.Retry(r => r.Exponential(4, 200 ms, 2 s)); TransferConsumer deliberately fails its
//      first attempts so a recorded observation with Attempts > 1 proves the policy re-delivered.
//   2. Routing-key patterns inside the definition — consumer.RoutingKeys("transfer.eu.*", "transfer.eu.priority")
//      is declared on the definition rather than inline at the endpoint.
//   3. Opt-in transport topology — c.DeclareTopology(...) declares the exchange/queue/binding for
//      this consumer only when called; the default manual-topology behavior is unchanged for
//      consumers that never call it.
//
// The definition is discovered ONLY via explicit DI registration below (no assembly scanning). The
// consumer is still registered on the receive endpoint via e.Consumer<TransferConsumer, TransferInitiated>;
// the DI-registered definition merges its routing keys and retry policy into that registration at
// bus start-up.
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   When running via Aspire AppHost, the connection string is injected automatically.

using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.RabbitMQ;
using BareWire.Samples.ConsumerDefinitionShowcase.Consumers;
using BareWire.Samples.ConsumerDefinitionShowcase.Definitions;
using BareWire.Samples.ConsumerDefinitionShowcase.Messages;
using BareWire.Samples.ConsumerDefinitionShowcase.Services;
using BareWire.Samples.ServiceDefaults;
using BareWire.Serialization.Json;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// 1. Shared defaults: OpenTelemetry observability + health checks
// ─────────────────────────────────────────────────────────────────────────────

builder.AddServiceDefaults();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Configuration
// ─────────────────────────────────────────────────────────────────────────────

string rabbitMqConnectionString =
    builder.Configuration.GetConnectionString("rabbitmq")
    ?? "amqp://guest:guest@localhost:5672/";

// ─────────────────────────────────────────────────────────────────────────────
// 3. Application services
// ─────────────────────────────────────────────────────────────────────────────

// Thread-safe sink combining the per-transfer attempt counter with the retry-proof observation
// recorded once TransferConsumer succeeds.
builder.Services.AddSingleton<TransferObservations>();

// Raw-AMQP upstream producer (simulates a non-BareWire publisher targeting the exchange declared
// opt-in by DeclareTopology below).
builder.Services.AddSingleton<TransferPublisher>();

// The consumer is resolved per-message by the BareWire dispatcher (transient lifetime).
builder.Services.AddTransient<TransferConsumer>();

// DI discovery — explicit registration only, no assembly scanning: the core resolves this
// ConsumerDefinition<TransferConsumer> once at bus start-up and merges its routing keys + retry
// policy into TransferConsumer's endpoint registration below.
builder.Services.AddSingleton<ConsumerDefinition<TransferConsumer>, TransferConsumerDefinition>();

// ─────────────────────────────────────────────────────────────────────────────
// 4. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Raw-first: System.Text.Json serializer with no envelope by default.
builder.Services.AddBareWireJsonSerializer();

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    rmq.Host(rabbitMqConnectionString);

    // One receive endpoint, one consumer. The routing-key patterns and retry policy are NOT
    // declared here — they live on TransferConsumerDefinition and are merged in at bus start-up
    // via the DI registration above.
    rmq.ReceiveEndpoint("consumer-definition-showcase.transfers", e =>
    {
        e.Consumer<TransferConsumer, TransferInitiated>(c =>
            // Opt-in transport topology (seam): declares the exchange/queue/binding for this
            // consumer only — the default manual-topology behavior is otherwise unchanged.
            // TransferPublisher.cs declares the SAME exchange with identical parameters
            // (type=topic, durable=false, autoDelete=false); any mismatch causes PRECONDITION_FAILED.
            c.DeclareTopology(
                exchange: "consumer-definition-showcase.exchange",
                queue: "consumer-definition-showcase.transfers",
                bindingKey: "transfer.#",
                exchangeType: ExchangeType.Topic,
                durable: false));
    });
};

builder.Services.AddBareWireWithRabbitMq(configureRabbitMq);

// ─────────────────────────────────────────────────────────────────────────────
// 5. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// ─────────────────────────────────────────────────────────────────────────────
// 6. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoints: /health, /health/live, /health/ready.
// Required by the Aspire fixture (WaitForResourceAsync checks the Running state).
app.MapServiceDefaults();

// POST /run — publishes one delivery on "transfer.eu.priority" and waits (up to 30 s) for
// TransferConsumer to record its observation once it has succeeded after retrying.
//
// SEC: log templates use structured parameters — NO string interpolation. Routing keys are not
// logged directly (producer-controlled, unauthenticated input — consistent with the transport
// core convention).
app.MapPost("/run", async (
    TransferObservations observations,
    TransferPublisher publisher,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    string runId = Guid.NewGuid().ToString("N");

    // Reset before publishing to isolate this run from any stale observations.
    observations.Reset(runId);

    await publisher.PublishTransferAsync(runId, cancellationToken).ConfigureAwait(false);

    // Wait up to 30 s for TransferConsumer to record its observation. Timeout is shorter than the
    // E2E test CTS (60 s) so the caller sees a partial result rather than a cancellation exception
    // on slow environments.
    IReadOnlyList<TransferObservation> obs = await observations.WaitForAsync(
        runId,
        expected: 1,
        timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

    RunLog.LogCompleted(logger, runId, obs.Count);

    return Results.Ok(new
    {
        runId,
        observations = obs.Select(o => new
        {
            routingKey = o.RoutingKey,
            consumer = o.ConsumerName,
            attempts = o.Attempts,
            transferId = o.TransferId,
        }),
    });
})
.Produces(StatusCodes.Status200OK)
.WithName("Run");

app.Run();

internal static partial class RunLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "ConsumerDefinitionShowcase: run {RunId} completed with {ObservationCount} observations")]
    public static partial void LogCompleted(ILogger logger, string runId, int observationCount);
}
