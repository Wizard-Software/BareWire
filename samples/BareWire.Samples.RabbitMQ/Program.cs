// BareWire.Samples.RabbitMQ — end-to-end sample demonstrating BareWire messaging patterns.
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: ConfigureConsumeTopology = false (default), topology declared explicitly.
//   - ADR-006  Publish-side back-pressure: PublishFlowControlOptions configured.
//   - Transactional outbox (BareWire.Outbox + EF Core) for reliable message delivery.
//   - OpenTelemetry observability (traces + metrics).
//   - Health checks exposing bus liveness at /health.
//   - Minimal API endpoint POST /orders that publishes OrderCreated.
//
// Architecture:
//   POST /orders → OrderCreated → exchange:rmq-sample.order.events (fanout) → queue:orders
//       └→ OrderConsumer → OrderProcessed → exchange:order-processed.events (fanout)
//   (SAGA state machine is in the separate BareWire.Samples.SagaOrderFlow sample.)
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker at amqp://guest:guest@localhost:5672/
//   - SQLite database file (created automatically on first run)

using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire;
using BareWire.Transport.RabbitMQ;
using BareWire.Samples.RabbitMQ.Models;
using BareWire.Observability;
using BareWire.Outbox.EntityFramework;
using BareWire.Samples.RabbitMQ.Consumers;
using BareWire.Samples.RabbitMQ.Messages;
using BareWire.Serialization.Json;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// 1. Configuration
// ─────────────────────────────────────────────────────────────────────────────

string rabbitMqConnectionString =
    builder.Configuration.GetConnectionString("rabbitmq")
    ?? "amqp://guest:guest@localhost:5672/";

string dbConnectionString =
    builder.Configuration.GetConnectionString("BareWire")
    ?? "Data Source=barewire-sample.db";

// ─────────────────────────────────────────────────────────────────────────────
// 2. Publish flow control (ADR-006)
// ─────────────────────────────────────────────────────────────────────────────

// Bounded outgoing channel: back-pressure applied when the publish buffer is near capacity.
// Register before AddBareWire so that AddBareWire's conditional registration (TryAdd) is skipped.
builder.Services.AddSingleton(new PublishFlowControlOptions
{
    MaxPendingPublishes = 1_000,
});

// ─────────────────────────────────────────────────────────────────────────────
// 3. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — AddBareWireJsonSerializer registers SystemTextJsonSerializer
// (IMessageSerializer) and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
// No envelope wrapper is added by default.
builder.Services.AddBareWireJsonSerializer();

// Register consumers in DI (resolved per-message by ConsumerDispatcher).
builder.Services.AddTransient<OrderConsumer>();

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    // Connection to the RabbitMQ broker.
    rmq.Host(rabbitMqConnectionString);
    rmq.DefaultExchange("rmq-sample.order.events");

    // ADR-002: Manual topology — declare all exchanges, queues, and bindings explicitly.
    // The broker resources are deployed by IBusControl.DeployTopologyAsync on startup.
    rmq.ConfigureTopology(t =>
    {
        // Two separate exchanges prevent a publish cycle:
        //   "rmq-sample.order.events" — receives OrderCreated from the HTTP endpoint (DefaultExchange).
        //   "order-processed.events"  — receives OrderProcessed from OrderConsumer via GetSendEndpoint.
        // Only "rmq-sample.order.events" is bound to the "orders" queue; "order-processed.events" is intentionally
        // left unbound here so that OrderProcessed messages do not loop back through OrderConsumer.
        // The "order-saga" queue is managed by BareWire.Samples.SagaOrderFlow — not this sample.
        t.DeclareExchange("rmq-sample.order.events", ExchangeType.Fanout, durable: true);
        t.DeclareExchange("order-processed.events", ExchangeType.Fanout, durable: true);

        // Queue for OrderConsumer (processes OrderCreated → sends OrderProcessed).
        t.DeclareQueue("orders", durable: true);
        t.BindExchangeToQueue("rmq-sample.order.events", "orders", routingKey: "#");
        // NOTE: "order-processed.events" is NOT bound to "orders" — that is what breaks the cycle.
    });

    // Endpoint: OrderConsumer processes OrderCreated messages.
    // ConfigureConsumeTopology defaults to false (ADR-002: manual topology).
    rmq.ReceiveEndpoint("orders", e =>
    {
        e.PrefetchCount = 16;
        e.ConcurrentMessageLimit = 8;
        e.RetryCount = 3;
        e.RetryInterval = TimeSpan.FromSeconds(5);
        e.Consumer<OrderConsumer, OrderCreated>();
    });
};

// NOTE: cfg.UseSerializer<T>() is a placeholder API for when the bus needs to know
// the serializer type at configuration time (e.g. for type-discriminator headers).
// The actual IMessageSerializer is already registered above via AddBareWireJsonSerializer().

builder.Services.AddBareWireRabbitMq(configureRabbitMq);
builder.Services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(configureRabbitMq);
});

// ─────────────────────────────────────────────────────────────────────────────
// 4. Transactional outbox / inbox (EF Core + SQLite)
// ─────────────────────────────────────────────────────────────────────────────

// ADR-005 / Phase 5: transactional outbox for at-least-once delivery guarantees.
// OutboxDispatcher polls the outbox table and forwards pending messages to the bus.
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options.UseSqlite(dbConnectionString),
    configureOutbox: outbox =>
    {
        outbox.PollingInterval = TimeSpan.FromSeconds(1);
        outbox.DispatchBatchSize = 100;

        // Automatically create Outbox/Inbox tables at host startup (development convenience).
        outbox.AutoCreateSchema = true;
    });

// ─────────────────────────────────────────────────────────────────────────────
// 6. Observability — OpenTelemetry (traces + metrics) + health checks
// ─────────────────────────────────────────────────────────────────────────────

// AddBareWireObservability replaces NullInstrumentation with BareWireInstrumentation
// and registers BareWireHealthCheck on the standard health check pipeline.
// Configure OTEL_EXPORTER_OTLP_ENDPOINT via environment variable for the OTLP exporter.
builder.Services.AddBareWireObservability(cfg =>
{
    cfg.EnableOpenTelemetry = true;

    // Uncomment to push traces/metrics via OTLP (e.g. to Jaeger or OTEL Collector):
    // cfg.UseOtlpExporter();
    //   — or set OTEL_EXPORTER_OTLP_ENDPOINT env-var for zero-code export.
});

// Expose health endpoints: /health (combined), /health/live, /health/ready.
builder.Services.AddHealthChecks();

// ─────────────────────────────────────────────────────────────────────────────
// 7. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// ─────────────────────────────────────────────────────────────────────────────
// 8. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoint — reflects BareWire bus health + system health.
app.MapHealthChecks("/health");

// POST /orders — accepts an order submission and publishes OrderCreated to the bus.
// OrderConsumer receives the event via the "rmq-sample.order.events" exchange.
app.MapPost("/orders", async (
    OrderRequest request,
    IPublishEndpoint bus,
    CancellationToken cancellationToken) =>
{
    string orderId = Guid.NewGuid().ToString();

    await bus.PublishAsync(
        new OrderCreated(orderId, request.Amount, request.Currency),
        cancellationToken).ConfigureAwait(false);

    return Results.Accepted($"/orders/{orderId}", new { OrderId = orderId });
});

// GET /orders/{id} — placeholder showing how a query endpoint would look.
// In production: query the saga repository for the current state.
app.MapGet("/orders/{id}", (string id) =>
    Results.Ok(new { OrderId = id, Status = "Use the saga repository to query state." }));

app.Run();
