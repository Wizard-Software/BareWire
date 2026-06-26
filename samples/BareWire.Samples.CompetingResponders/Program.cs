// BareWire.Samples.CompetingResponders — demonstrates publish-style request/response with
// competing responders and first-in-wins semantics, running as two replicas via Aspire WithReplicas.
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: explicit fanout exchange + per-instance responder queue declarations.
//   - ADR-027  Publish-style request/response + first-in-wins:
//              PublishRequest<PingRequest>() routes requests via a per-type fanout exchange so that
//              every bound responder queue receives a copy. The IRequestClient<T> caller resolves
//              exactly ONE response (first-in-wins via TrySetResult idempotency); remaining N-1
//              responses are silently dropped at Debug level.
//   - Competing responders via WithReplicas(2): each replica binds its own unique queue to the
//              fanout exchange, ensuring broadcast delivery (not competing-consumer load-balancing).
//
// Three caveats (m6):
//   1. CorrelationId echo: RespondAsync routes back via the ReplyTo header; CorrelationId is
//      echoed automatically by the framework — no manual correlation required.
//   2. Reply-queue flow: the caller-side reply queue operates outside ADR-004 credit-based
//      flow control (autoAck path); backpressure applies to the outbound publish only.
//   3. First-in-wins drops N-1 RESPONSES, not N-1 EXECUTIONS: all N replicas fully process the
//      request (side effects run N times). This sample's side effect is a Debug log — idempotent.
//   4. Per-instance responder queues are declared autoDelete:true so that orphaned queues are
//      reclaimed by the broker when a responder disconnects (no queue leak on long-lived brokers).
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   When running via Aspire AppHost, the connection string is injected automatically.

using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire;
using BareWire.RabbitMQ;
using BareWire.Transport.RabbitMQ;
using BareWire.Samples.CompetingResponders.Consumers;
using BareWire.Samples.CompetingResponders.Messages;
using BareWire.Samples.ServiceDefaults;
using BareWire.Serialization.Json;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// 1. Shared defaults: OpenTelemetry observability + health checks
// ─────────────────────────────────────────────────────────────────────────────

builder.AddServiceDefaults();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Instance identity — computed once per process, stable for its lifetime.
//    RESPONDER_ID injected by Aspire per-replica (e.g. "0", "1") or falls back
//    to a short random suffix when running without Aspire.
// ─────────────────────────────────────────────────────────────────────────────

string responderId =
    Environment.GetEnvironmentVariable("RESPONDER_ID")
    ?? Guid.NewGuid().ToString("N")[..8];

builder.Services.AddSingleton(new ResponderIdentity(responderId));

// ─────────────────────────────────────────────────────────────────────────────
// 3. Configuration
// ─────────────────────────────────────────────────────────────────────────────

string rabbitMqConnectionString =
    builder.Configuration.GetConnectionString("rabbitmq")
    ?? "amqp://guest:guest@localhost:5672/";

// ─────────────────────────────────────────────────────────────────────────────
// 4. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
builder.Services.AddBareWireJsonSerializer();

// Register consumer in DI (resolved per-message by ConsumerDispatcher, transient lifetime).
builder.Services.AddTransient<PingResponderConsumer>();

// Per-instance responder queue name. Unique per replica so the fanout exchange delivers
// a copy to every responder (competing responders, not competing consumers).
// autoDelete:true ensures the queue is reclaimed when this instance disconnects.
string responderQueue = $"competing-responders-{responderId}";

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    // Connection to the RabbitMQ broker.
    rmq.Host(rabbitMqConnectionString);

    // ADR-002: Manual topology — ConfigureTopology MUST be called before PublishRequest<T>()
    // because bus startup validation (ValidatePublishRequestMappings) requires the per-type
    // fanout exchange to be present in the declared topology.
    rmq.ConfigureTopology(t =>
    {
        // Declare the per-type fanout exchange (name = "Namespace:TypeName" convention,
        // durable, non-auto-delete, Fanout). DeclareRequestExchange<T> auto-derives the name.
        t.DeclareRequestExchange<PingRequest>();

        // Declare the per-instance responder queue: non-durable (not needed across broker
        // restarts), autoDelete:true (broker reclaims on disconnect — no orphaned queues).
        t.DeclareQueue(responderQueue, durable: false, autoDelete: true, configure: _ => { });

        // Bind the responder queue to the fanout exchange with an empty routing key.
        // Fanout exchanges ignore the routing key; the binding delivers every message
        // published to the exchange to this queue.
        t.BindRequestExchangeToQueue<PingRequest>(responderQueue);
    });

    // Enable publish-style routing for PingRequest. The exchange name is derived from the
    // Namespace:TypeName convention, matching what DeclareRequestExchange<T> declared above.
    rmq.PublishRequest<PingRequest>();

    // Consume from the per-instance responder queue.
    rmq.ReceiveEndpoint(responderQueue, e =>
    {
        e.Consumer<PingResponderConsumer, PingRequest>();
    });
};

// Single-call registration (ADR-028): the BareWire.RabbitMQ bundle wires the core engine and the
// RabbitMQ transport in one statement (equivalent to AddBareWireRabbitMq(...) + AddBareWire(...)).
builder.Services.AddBareWireWithRabbitMq(configureRabbitMq);

// ─────────────────────────────────────────────────────────────────────────────
// 5. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// ─────────────────────────────────────────────────────────────────────────────
// 6. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoints: /health, /health/live, /health/ready.
app.MapServiceDefaults();

// POST /ask?payload=<text> — publishes a PingRequest to the per-type fanout exchange and
// waits for the first response (first-in-wins). Returns the echo and the winning responder's id.
//
// With WithReplicas(2) both replicas receive a copy of each request via the fanout exchange.
// Both call RespondAsync; the framework delivers the first arriving response to the caller
// and silently drops the second (TrySetResult idempotency). The winning ResponderId varies
// across requests depending on which replica is faster.
//
// SEC-D5: payload is user-supplied; log templates use structured parameters — NO interpolation.
app.MapPost("/ask", async (
    string? payload,
    IBus bus,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    string requestPayload = payload ?? "ping";

    IRequestClient<PingRequest> client =
        await bus.CreateRequestClientAsync<PingRequest>(cancellationToken).ConfigureAwait(false);

    Response<PingResponse> response = await client
        .GetResponseAsync<PingResponse>(new PingRequest(requestPayload), cancellationToken)
        .ConfigureAwait(false);

    AskLog.LogAnswered(logger, requestPayload, response.Message.ResponderId);

    return Results.Ok(new
    {
        response.Message.Echo,
        response.Message.ResponderId,
    });
})
.Produces(StatusCodes.Status200OK)
.WithName("Ask");

app.Run();

internal static partial class AskLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Request {Payload} answered by responder {ResponderId} (first-in-wins)")]
    public static partial void LogAnswered(ILogger logger, string payload, string responderId);
}
