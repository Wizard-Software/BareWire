// BareWire.Samples.MultiConsumerPartitioning — demonstrates multiple consumers on a single
// endpoint with PartitionerMiddleware for per-CorrelationId sequential processing.
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: topic exchange with a single queue receiving all event types.
//   - ADR-004  Credit-based flow control: ConcurrentMessageLimit = 16 allows high throughput,
//              while per-endpoint OrderedByHeader("ordering-key") guarantees sequential processing
//              per key. The inbound runner reads the "ordering-key" header before deserialization
//              and maps it to a fixed lane (fixed-lane hashing); messages with different keys run
//              fully in parallel across lanes, messages sharing a key are serialized within their lane.
//   - Multiple typed consumers (OrderEventConsumer, PaymentEventConsumer, ShipmentEventConsumer)
//     registered on a single endpoint — ConsumerDispatcher routes by message CLR type.
//   - PostgreSQL persistence: ProcessingLogEntry records timestamp and ThreadId per message,
//     enabling offline verification that per-CorrelationId ordering was maintained.
//
// Per-key consumer ordering (ADR-026) replaces the deprecated DI-level AddPartitionerMiddleware.
// The producer stamps the key in the "ordering-key" transport header; the endpoint opts in with
// OrderedByHeader("ordering-key"). Per-key ordering is OFF by default.
//
// Architecture:
//   POST /events/generate (1000 events, 10 CorrelationIds, "ordering-key" header per message)
//       └→ IBus.PublishAsync(msg, headers) → RabbitMQ exchange "events" (topic, durable)
//               └→ queue "event-processing" (binding: #)
//                       └→ OrderedByHeader("ordering-key") → fixed-lane hashing (lanes = 16)
//                              ├→ OrderEventConsumer    (message type: OrderEvent)
//                              ├→ PaymentEventConsumer  (message type: PaymentEvent)
//                              └→ ShipmentEventConsumer (message type: ShipmentEvent)
//                                     └→ ProcessingLogEntry → PostgreSQL
//
// Topic exchange + routing keys:
//   PublishAsync<T> derives the routing key from the CLR type's FullName.
//   The queue binding uses "#" (match all) so every published message type is delivered
//   to "event-processing" regardless of the routing key format.
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   - PostgreSQL server (default: Host=localhost;Database=barewiredb;Username=postgres;Password=postgres)
//   When running via Aspire AppHost, both are provisioned automatically.

using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire;
using BareWire.Transport.RabbitMQ;
using BareWire.Samples.MultiConsumerPartitioning.Consumers;
using BareWire.Samples.MultiConsumerPartitioning.Data;
using BareWire.Samples.MultiConsumerPartitioning.Messages;
using BareWire.Samples.ServiceDefaults;
using BareWire.Serialization.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

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

string dbConnectionString =
    builder.Configuration.GetConnectionString("barewiredb")
    ?? "Host=localhost;Database=barewiredb;Username=postgres;Password=postgres";

// ─────────────────────────────────────────────────────────────────────────────
// 3. EF Core — PostgreSQL persistence for processing log entries
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddDbContext<PartitionDbContext>(o => o.UseNpgsql(dbConnectionString));

// ─────────────────────────────────────────────────────────────────────────────
// 4. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
builder.Services.AddBareWireJsonSerializer();

// Register the three consumers in DI (resolved per-message by ConsumerDispatcher).
builder.Services.AddTransient<OrderEventConsumer>();
builder.Services.AddTransient<PaymentEventConsumer>();
builder.Services.AddTransient<ShipmentEventConsumer>();

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    // Connection to the RabbitMQ broker.
    rmq.Host(rabbitMqConnectionString);
    rmq.DefaultExchange("events");

    // ADR-002: Manual topology — declare all exchanges, queues, and bindings explicitly.
    rmq.ConfigureTopology(t =>
    {
        // Topic exchange — messages are routed by CLR type FullName as the routing key.
        // Binding uses "#" (wildcard: match everything) so all three event types
        // (OrderEvent, PaymentEvent, ShipmentEvent) are delivered to "event-processing".
        t.DeclareExchange("events", ExchangeType.Topic, durable: true);

        t.DeclareQueue("event-processing", durable: true);
        t.BindExchangeToQueue("events", "event-processing", routingKey: "#");
    });

    // Single endpoint — three consumers registered side-by-side.
    // ConsumerDispatcher dispatches each inbound message to the matching IConsumer<T>
    // based on the deserialized CLR type.
    // ConcurrentMessageLimit = 16: up to 16 messages are in-flight simultaneously.
    // Per-key consumer ordering: the inbound runner reads the "ordering-key" header BEFORE
    // deserialization, hashes it to a fixed lane, and processes each lane sequentially. Messages
    // sharing an ordering key are serialized within their lane; messages with different keys run in
    // parallel across lanes (up to the lane count = ConcurrentMessageLimit). Per-key ordering is OFF
    // by default — this opt-in call replaces the deprecated DI-level AddPartitionerMiddleware.
    //
    // This sample is single-instance (one consumer process), so it declares the LocalPartitioned
    // strategy explicitly. The default strategy (Auto) is capability-driven and fails fast on
    // RabbitMQ unless a TransportAffinity (SAC / ConsistentHash) is declared for cross-instance
    // ordering — that multi-instance scenario is out of scope here (see R8.15/R8.17).
    rmq.ReceiveEndpoint("event-processing", e =>
    {
        e.ConcurrentMessageLimit = 16;
        e.OrderedBy(o => o
            .ByHeader("ordering-key")
            .Strategy(ConsumerOrderingStrategy.LocalPartitioned));
        e.Consumer<OrderEventConsumer, OrderEvent>();
        e.Consumer<PaymentEventConsumer, PaymentEvent>();
        e.Consumer<ShipmentEventConsumer, ShipmentEvent>();
    });
};

builder.Services.AddBareWireRabbitMq(configureRabbitMq);
builder.Services.AddBareWire(cfg =>
{
    // Per-key consumer ordering is configured per endpoint via e.OrderedByHeader("ordering-key")
    // above (see the ReceiveEndpoint block). The deprecated DI-level AddPartitionerMiddleware is
    // no longer used — the inbound runner derives the ordering key from the "ordering-key" header
    // and serializes processing on a fixed lane.
    // UseRabbitMQ is a deprecated no-op (Feature 15, ADR-028 D4); transport comes from AddBareWireRabbitMq.
    // Migration to AddBareWireWithRabbitMq is task 15.11; CS0618 suppressed here to keep the build green.
#pragma warning disable CS0618 // Type or member is obsolete
    cfg.UseRabbitMQ(configureRabbitMq);
#pragma warning restore CS0618 // Type or member is obsolete
});

// ─────────────────────────────────────────────────────────────────────────────
// 5. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// Development only — use migrations in production.
// EnsureCreatedAsync is a no-op when the database already exists (e.g. created by another sample
// sharing the same connection string). CreateTablesAsync adds missing tables for this DbContext.
using (IServiceScope scope = app.Services.CreateScope())
{
    PartitionDbContext db = scope.ServiceProvider.GetRequiredService<PartitionDbContext>();
    await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
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
// 6. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoints: /health, /health/live, /health/ready.
app.MapServiceDefaults();

// POST /events/generate — publishes a burst of 1000 events distributed across 10 CorrelationIds.
// Each message carries the CorrelationId in the "ordering-key" transport header. The mix of
// OrderEvent, PaymentEvent, and ShipmentEvent messages demonstrates that per-endpoint
// OrderedByHeader("ordering-key") serializes processing per key (fixed-lane) while allowing full
// parallelism across different keys.
app.MapPost("/events/generate", async (
    IPublishEndpoint bus,
    CancellationToken cancellationToken) =>
{
    const int totalEvents = 1_000;
    const int correlationIdCount = 10;

    // Pre-generate the fixed set of CorrelationIds used for this burst.
    Guid[] correlationIds = new Guid[correlationIdCount];
    for (int i = 0; i < correlationIdCount; i++)
        correlationIds[i] = Guid.NewGuid();

    int published = 0;
    for (int i = 0; i < totalEvents; i++)
    {
        string correlationId = correlationIds[i % correlationIdCount].ToString();
        DateTime now = DateTime.UtcNow;

        // Stamp the ordering key on the transport header. The consumer endpoint is configured with
        // OrderedByHeader("ordering-key"): the inbound runner reads this header before deserialization
        // and serializes processing per key on a fixed lane, preserving per-correlation order at runtime.
        var headers = new Dictionary<string, string> { ["ordering-key"] = correlationId };

        // Round-robin across the three event types.
        int eventKind = i % 3;
        switch (eventKind)
        {
            case 0:
                await bus.PublishAsync(
                    new OrderEvent($"ORD-{i:D6}", correlationId, now),
                    headers, cancellationToken).ConfigureAwait(false);
                break;
            case 1:
                await bus.PublishAsync(
                    new PaymentEvent($"PAY-{i:D6}", correlationId, now),
                    headers, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await bus.PublishAsync(
                    new ShipmentEvent($"SHP-{i:D6}", correlationId, now),
                    headers, cancellationToken).ConfigureAwait(false);
                break;
        }

        published++;
    }

    return Results.Accepted(value: new
    {
        Published = published,
        CorrelationIds = correlationIds.Select(id => id.ToString()).ToArray(),
    });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("GenerateEvents");

// GET /events/processing-log — returns all ProcessingLogEntry rows ordered by ProcessedAt.
// Use this endpoint to verify that messages sharing a CorrelationId were processed sequentially
// on a consistent partition (i.e. all entries with the same CorrelationId have non-overlapping
// ProcessedAt timestamps).
app.MapGet("/events/processing-log", async (
    PartitionDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    List<ProcessingLogEntry> entries = await dbContext.ProcessingLog
        .OrderBy(e => e.ProcessedAt)
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(entries);
})
.Produces<List<ProcessingLogEntry>>()
.WithName("GetProcessingLog");

app.Run();
