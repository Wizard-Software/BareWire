// BareWire.Samples.ConsumerRoutingKeys — demonstrates consume-time routing-key dispatch
// with three consumers sharing a single queue.
//
// A topic exchange delivers all traffic to one shared queue (binding "#"); the BareWire
// dispatcher selects the correct consumer client-side by matching the delivery's routing key
// against the patterns declared per consumer. Broker topology does not segregate the traffic.
//
// What this sample shows:
//   1. One shared queue, many consumers — a single queue bound to a topic exchange with "#";
//      the BareWire dispatcher (not the broker) determines which consumer handles each delivery.
//   2. Most-specific-wins — "transfer.eu.priority" (exact pattern) beats "transfer.eu.*"
//      (wildcard) for priority deliveries. Standard EU deliveries reach only the wildcard consumer.
//   3. Type-less interop with AcceptUntyped() — a raw producer publishes plain JSON with no
//      BW-MessageType header; the consumer opted in via AcceptUntyped() receives the delivery and
//      deserializes it raw-first into LegacyNotification (explicit opt-in, secure-by-default).
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   When running via Aspire AppHost, the connection string is injected automatically.

using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.RabbitMQ;
using BareWire.Samples.ConsumerRoutingKeys.Consumers;
using BareWire.Samples.ConsumerRoutingKeys.Messages;
using BareWire.Samples.ConsumerRoutingKeys.Services;
using BareWire.Samples.ServiceDefaults;
using BareWire.Serialization.Json;
using BareWire.Transport.RabbitMQ;

// BareWire.Abstractions.ExchangeType is the BareWire enum used for topology declarations below.
// UpstreamPublisher.cs uses RabbitMQ.Client.ExchangeType (a static class with string constants)
// in its own file — no ambiguous-reference collision between the two.

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

// Thread-safe observation sink — records which consumer handled each delivery per run.
builder.Services.AddSingleton<RoutingObservations>();

// Raw-AMQP upstream producer (simulates a non-BareWire publisher with per-message routing keys).
builder.Services.AddSingleton<UpstreamPublisher>();

// Consumers are resolved per-message by the BareWire dispatcher (transient lifetime).
builder.Services.AddTransient<RegionTransferConsumer>();
builder.Services.AddTransient<PriorityTransferConsumer>();
builder.Services.AddTransient<LegacyNotificationConsumer>();

// ─────────────────────────────────────────────────────────────────────────────
// 4. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Raw-first: System.Text.Json serializer with no envelope by default.
builder.Services.AddBareWireJsonSerializer();

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    rmq.Host(rabbitMqConnectionString);

    // Manual topology: one topic exchange, one shared queue, one catch-all binding.
    // All deliveries land in the shared queue regardless of routing key; the BareWire
    // dispatcher selects the consumer client-side by pattern-matching the routing key.
    rmq.ConfigureTopology(t =>
    {
        // durable:false / autoDelete:true — clean state per AppHost session.
        // UpstreamPublisher declares the same exchange with identical parameters;
        // any mismatch causes PRECONDITION_FAILED on the broker.
        t.DeclareExchange(
            "consumer-routing-keys.transfers",
            ExchangeType.Topic,
            durable: false,
            autoDelete: true);

        t.DeclareQueue(
            "consumer-routing-keys.shared",
            durable: false,
            autoDelete: true,
            configure: _ => { });

        // Bind with "#" — the shared queue receives every delivery published to the exchange.
        // Consumer selection is purely client-side routing-key pattern matching.
        t.BindExchangeToQueue(
            "consumer-routing-keys.transfers",
            "consumer-routing-keys.shared",
            routingKey: "#");
    });

    // One receive endpoint, three consumers with different routing-key patterns:
    //
    //   RegionTransferConsumer:     "transfer.eu.*"        (wildcard — any EU transfer kind)
    //   PriorityTransferConsumer:   "transfer.eu.priority" (exact   — beats wildcard for priority)
    //   LegacyNotificationConsumer: "legacy.#"             (type-less AcceptUntyped — no BW-MessageType)
    rmq.ReceiveEndpoint("consumer-routing-keys.shared", e =>
    {
        e.Consumer<RegionTransferConsumer, TransferInitiated>(c =>
            c.RoutingKey("transfer.eu.*"));

        // Exact pattern — wins over "transfer.eu.*" for routing key "transfer.eu.priority".
        e.Consumer<PriorityTransferConsumer, TransferInitiated>(c =>
            c.RoutingKey("transfer.eu.priority"));

        e.Consumer<LegacyNotificationConsumer, LegacyNotification>(c =>
        {
            c.RoutingKey("legacy.#");
            // Explicit opt-in required for type-less dispatch (deliveries without BW-MessageType).
            // Without AcceptUntyped(), this consumer is never a candidate for foreign JSON —
            // typed consumers are never silently exposed to untrusted raw payloads.
            c.AcceptUntyped();
        });
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

// POST /run — publishes 3 deliveries to the topic exchange and waits (up to 30 s) for all
// three consumers to record their dispatch observations, then returns a summary.
//
// SEC: payload is produced by UpstreamPublisher (controlled input for a demo); log templates use
// structured parameters — NO string interpolation. Routing keys are not logged directly
// (producer-controlled, unauthenticated input — consistent with the transport core convention).
app.MapPost("/run", async (
    RoutingObservations observations,
    UpstreamPublisher publisher,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    string runId = Guid.NewGuid().ToString("N");

    // Reset before publishing to isolate this run from any stale observations.
    observations.Reset(runId);

    await publisher.PublishScenarioAsync(runId, cancellationToken).ConfigureAwait(false);

    // Wait up to 30 s for all 3 consumers to record their observations.
    // Timeout is shorter than the E2E test CTS (60 s) so the caller sees a partial result
    // rather than a cancellation exception on slow environments.
    IReadOnlyList<RoutingObservation> obs = await observations.WaitForAsync(
        runId,
        expected: 3,
        timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

    RunLog.LogCompleted(logger, runId, obs.Count);

    return Results.Ok(new
    {
        runId,
        observations = obs.Select(o => new
        {
            routingKey = o.RoutingKey,
            consumer = o.ConsumerName,
            messageType = o.MessageType,
            typeLess = o.TypeLess,
            echo = o.Echo,
        }),
    });
})
.Produces(StatusCodes.Status200OK)
.WithName("Run");

app.Run();

internal static partial class RunLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "ConsumerRoutingKeys: run {RunId} completed with {ObservationCount} observations")]
    public static partial void LogCompleted(ILogger logger, string runId, int observationCount);
}
