// BareWire.Samples.MultiConsumerPartitioning — demonstrates multiple consumers on a single
// endpoint with PartitionerMiddleware for per-CorrelationId sequential processing.
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: topic exchange with a single queue receiving all event types.
//   - ADR-004  Credit-based flow control: ConcurrentMessageLimit = 16 allows high throughput,
//              while PartitionerMiddleware (64 partitions) guarantees sequential processing per
//              CorrelationId. Messages with different CorrelationIds run fully in parallel;
//              messages sharing a CorrelationId are serialized within their partition.
//   - Multiple typed consumers (OrderEventConsumer, PaymentEventConsumer, ShipmentEventConsumer)
//     registered on a single endpoint — ConsumerDispatcher routes by message CLR type.
//   - PostgreSQL persistence: ProcessingLogEntry records timestamp and ThreadId per message,
//     enabling offline verification that per-CorrelationId ordering was maintained.
//
// Architecture:
//   POST /events/generate (1000 events, 10 CorrelationIds)
//       └→ IBus.PublishAsync → RabbitMQ exchange "events" (topic, durable)
//               └→ queue "event-processing" (binding: #)
//                       ├→ OrderEventConsumer    (message type: OrderEvent)
//                       ├→ PaymentEventConsumer  (message type: PaymentEvent)
//                       └→ ShipmentEventConsumer (message type: ShipmentEvent)
//                              └→ PartitionerMiddleware (64 partitions, key: CorrelationId header)
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
using BareWire.Core;
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
    // ConcurrentMessageLimit = 16: up to 16 messages are in-flight simultaneously;
    // PartitionerMiddleware further serializes messages within the same CorrelationId
    // partition so that per-correlation ordering is preserved even under high concurrency.
    rmq.ReceiveEndpoint("event-processing", e =>
    {
        e.ConcurrentMessageLimit = 16;
        e.Consumer<OrderEventConsumer, OrderEvent>();
        e.Consumer<PaymentEventConsumer, PaymentEvent>();
        e.Consumer<ShipmentEventConsumer, ShipmentEvent>();
    });
};

builder.Services.AddBareWireRabbitMq(configureRabbitMq);
builder.Services.AddBareWire(cfg =>
{
    // PartitionerMiddleware: 64 partitions, default key selector (reads CorrelationId header,
    // falls back to MessageId). Registered in DI with a factory so the custom partitionCount
    // is honoured. ServiceCollectionExtensions.RegisterMiddleware uses TryAddSingleton, so
    // this factory registration is not overwritten.
    builder.Services.AddPartitionerMiddleware(cfg, partitionCount: 64);

    cfg.UseRabbitMQ(configureRabbitMq);
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
// The mix of OrderEvent, PaymentEvent, and ShipmentEvent messages demonstrates that
// PartitionerMiddleware serializes processing per CorrelationId while allowing full parallelism
// across different CorrelationIds.
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

        // Round-robin across the three event types.
        int eventKind = i % 3;
        switch (eventKind)
        {
            case 0:
                await bus.PublishAsync(
                    new OrderEvent($"ORD-{i:D6}", correlationId, now),
                    cancellationToken).ConfigureAwait(false);
                break;
            case 1:
                await bus.PublishAsync(
                    new PaymentEvent($"PAY-{i:D6}", correlationId, now),
                    cancellationToken).ConfigureAwait(false);
                break;
            default:
                await bus.PublishAsync(
                    new ShipmentEvent($"SHP-{i:D6}", correlationId, now),
                    cancellationToken).ConfigureAwait(false);
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
