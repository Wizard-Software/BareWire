// BareWire.Samples.ObservabilityShowcase — demonstrates the full BareWire observability stack.
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: topic exchange + 4 queues declared explicitly.
//   - ADR-004  Credit-based flow control: inflight message tracking and health alerts.
//   - ADR-006  Publish-side back-pressure: PublishFlowControlOptions configured.
//   - OpenTelemetry: ActivitySource("BareWire") spans visible in Aspire Dashboard / Jaeger.
//   - Metrics: barewire.messages.published, barewire.messages.consumed, barewire.message.duration.
//   - Health checks: bus liveness exposed at /health.
//   - SAGA state machine: DemoSagaStateMachine (Initial → Processing → Completed) with PostgreSQL.
//   - Transactional outbox: reliable message delivery via OutboxDispatcher + EF Core.
//   - 3-hop distributed trace: publish → order consumer → payment consumer → shipment consumer.
//
// Architecture:
//   POST /demo/run → DemoOrderCreated (exchange: demo.events, routing key: order.created)
//       ├→ DemoOrderConsumer  (queue: demo-orders)  → publishes DemoPaymentProcessed
//       │       └→ DemoPaymentConsumer (queue: demo-payments) → publishes DemoShipmentDispatched
//       │               └→ DemoShipmentConsumer (queue: demo-shipments) → logs completion
//       └→ DemoSagaStateMachine (queue: demo-saga):
//                  Initial ──DemoOrderCreated──→ Processing
//                  Processing ──DemoPaymentProcessed──→ Completed (finalized)
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   - PostgreSQL server (default: Host=localhost;Database=barewiredb;Username=postgres;Password=postgres)
//   When running via Aspire AppHost, both are provisioned automatically.

using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Core;
using BareWire.Transport.RabbitMQ;
using BareWire.Observability;
using BareWire.Outbox.EntityFramework;
using BareWire.Saga;
using BareWire.Saga.EntityFramework;
using BareWire.Samples.ObservabilityShowcase.Consumers;
using BareWire.Samples.ObservabilityShowcase.Data;
using BareWire.Samples.ObservabilityShowcase.Messages;
using BareWire.Samples.ObservabilityShowcase.Saga;
using BareWire.Samples.ServiceDefaults;
using BareWire.Serialization.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// 1. Shared defaults: OpenTelemetry observability + health checks (Aspire)
// ─────────────────────────────────────────────────────────────────────────────

builder.AddServiceDefaults();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Configuration
// ─────────────────────────────────────────────────────────────────────────────

string rabbitMqConnectionString =
    builder.Configuration.GetConnectionString("rabbitmq")
    ?? "amqp://guest:guest@localhost:5672/";

string dbConnectionString =
    builder.Configuration.GetConnectionString("barewiredb")
    ?? "Host=localhost;Database=barewiredb;Username=postgres;Password=postgres";

// ─────────────────────────────────────────────────────────────────────────────
// 3. EF Core — application DbContext (PostgreSQL)
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddDbContext<ShowcaseDbContext>(o => o.UseNpgsql(dbConnectionString));

// ─────────────────────────────────────────────────────────────────────────────
// 4. Publish flow control (ADR-006)
// ─────────────────────────────────────────────────────────────────────────────

// Bounded outgoing channel: back-pressure applied when the publish buffer is near capacity.
// Register before AddBareWire so that AddBareWire's conditional registration (TryAdd) is skipped.
builder.Services.AddSingleton(new PublishFlowControlOptions
{
    MaxPendingPublishes = 500,
});

// ─────────────────────────────────────────────────────────────────────────────
// 5. BareWire messaging — serializer, consumers, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
// No envelope wrapper is added by default.
builder.Services.AddBareWireJsonSerializer();

// Register consumers in DI (resolved per-message by ConsumerDispatcher).
builder.Services.AddTransient<DemoOrderConsumer>();
builder.Services.AddTransient<DemoPaymentConsumer>();
builder.Services.AddTransient<DemoShipmentConsumer>();

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    rmq.Host(rabbitMqConnectionString);
    rmq.DefaultExchange("demo.events");

    // ADR-002: Manual topology — declare all exchanges, queues, and bindings explicitly.
    // Topic exchange routes messages by routing key pattern to the appropriate queues.
    rmq.ConfigureTopology(t =>
    {
        // Single topic exchange for all demo events.
        t.DeclareExchange("demo.events", ExchangeType.Topic, durable: true);

        // Queue for order events — receives messages with routing key matching "order.*".
        t.DeclareQueue("demo-orders", durable: true);
        t.BindExchangeToQueue("demo.events", "demo-orders", routingKey: "order.*");

        // Queue for payment events — receives messages with routing key matching "payment.*".
        t.DeclareQueue("demo-payments", durable: true);
        t.BindExchangeToQueue("demo.events", "demo-payments", routingKey: "payment.*");

        // Queue for shipment events — receives messages with routing key matching "shipment.*".
        t.DeclareQueue("demo-shipments", durable: true);
        t.BindExchangeToQueue("demo.events", "demo-shipments", routingKey: "shipment.*");

        // Queue for the saga — receives all events via catch-all "#" routing key.
        t.DeclareQueue("demo-saga", durable: true);
        t.BindExchangeToQueue("demo.events", "demo-saga", routingKey: "#");
    });

    // Endpoint: DemoOrderConsumer processes DemoOrderCreated and publishes DemoPaymentProcessed.
    rmq.ReceiveEndpoint("demo-orders", e =>
    {
        e.PrefetchCount = 16;
        e.ConcurrentMessageLimit = 8;
        e.RetryCount = 3;
        e.RetryInterval = TimeSpan.FromSeconds(1);
        e.Consumer<DemoOrderConsumer, DemoOrderCreated>();
    });

    // Endpoint: DemoPaymentConsumer processes DemoPaymentProcessed and publishes DemoShipmentDispatched.
    rmq.ReceiveEndpoint("demo-payments", e =>
    {
        e.PrefetchCount = 16;
        e.ConcurrentMessageLimit = 8;
        e.RetryCount = 3;
        e.RetryInterval = TimeSpan.FromSeconds(1);
        e.Consumer<DemoPaymentConsumer, DemoPaymentProcessed>();
    });

    // Endpoint: DemoShipmentConsumer logs pipeline completion.
    rmq.ReceiveEndpoint("demo-shipments", e =>
    {
        e.PrefetchCount = 16;
        e.ConcurrentMessageLimit = 8;
        e.RetryCount = 3;
        e.RetryInterval = TimeSpan.FromSeconds(1);
        e.Consumer<DemoShipmentConsumer, DemoShipmentDispatched>();
    });

    // Endpoint: DemoSagaStateMachine correlates DemoOrderCreated and DemoPaymentProcessed.
    rmq.ReceiveEndpoint("demo-saga", e =>
    {
        e.PrefetchCount = 8;
        e.ConcurrentMessageLimit = 4;
        e.StateMachineSaga<DemoSagaStateMachine>();
    });
};

builder.Services.AddBareWireRabbitMq(configureRabbitMq);
builder.Services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(configureRabbitMq);
});

// ─────────────────────────────────────────────────────────────────────────────
// 6. SAGA persistence (EF Core + PostgreSQL)
// ─────────────────────────────────────────────────────────────────────────────

// Persist DemoSagaState to PostgreSQL via EF Core.
// SagaDbContext is registered separately from ShowcaseDbContext.
builder.Services.AddBareWireSaga<DemoSagaState>(
    options => options.UseNpgsql(dbConnectionString));

// Register the state machine and wire its message dispatcher into the consume pipeline.
builder.Services.AddBareWireSagaStateMachine<DemoSagaStateMachine, DemoSagaState>();

// ─────────────────────────────────────────────────────────────────────────────
// 7. Transactional outbox / inbox (EF Core + PostgreSQL)
// ─────────────────────────────────────────────────────────────────────────────

// OutboxDispatcher polls the outbox table every second and forwards pending messages
// to the bus, providing at-least-once delivery guarantees.
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options.UseNpgsql(dbConnectionString),
    configureOutbox: outbox =>
    {
        outbox.PollingInterval = TimeSpan.FromSeconds(1);
        outbox.DispatchBatchSize = 100;

        // Automatically create Outbox/Inbox tables at host startup (development convenience).
        outbox.AutoCreateSchema = true;
    });

// ─────────────────────────────────────────────────────────────────────────────
// 9. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// Development only — use migrations in production.
// EnsureCreatedAsync is a no-op when the database already exists (e.g. created by another sample
// sharing the same connection string). CreateTablesAsync adds missing tables for this DbContext.
using (IServiceScope scope = app.Services.CreateScope())
{
    ShowcaseDbContext db = scope.ServiceProvider.GetRequiredService<ShowcaseDbContext>();
    try
    {
        var creator = db.Database.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>();
        await creator.CreateTablesAsync().ConfigureAwait(false);
    }
    catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P07")
    {
        // Tables already exist from a previous run — safe to ignore in development.
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 10. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoints: /health, /health/live, /health/ready.
app.MapServiceDefaults();

// POST /demo/run — publishes DemoOrderCreated to trigger the full observability pipeline.
// The complete 3-hop distributed trace (order → payment → shipment) is visible in
// the Aspire Dashboard or any OTLP-compatible tracing backend (Jaeger, Zipkin, etc.).
app.MapPost("/demo/run", async (
    IPublishEndpoint bus,
    CancellationToken cancellationToken) =>
{
    string orderId = Guid.NewGuid().ToString();
    decimal amount = Math.Round((decimal)(Random.Shared.NextDouble() * 999 + 1), 2);

    await bus.PublishAsync(
        new DemoOrderCreated(orderId, amount, CreatedAt: DateTime.UtcNow),
        cancellationToken).ConfigureAwait(false);

    return Results.Accepted(
        value: new
        {
            OrderId = orderId,
            Amount = amount,
            Message = "DemoOrderCreated published. Check Aspire Dashboard for distributed traces.",
        });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("RunDemo");

app.Run();
