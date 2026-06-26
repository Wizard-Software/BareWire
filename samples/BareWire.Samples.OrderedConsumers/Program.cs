// BareWire.Samples.OrderedConsumers — demonstrates per-key consumer ordering end-to-end with
// competing consumer instances (multi-replica via Aspire WithReplicas) and poison-head parking.
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: explicit exchange/queue declarations with x-single-active-consumer.
//   - ADR-025  Transactional Outbox (PerKey): producer stamps "ordering-key" header; outbox preserves
//              per-key head-of-line ordering at dispatch time.
//   - ADR-026  Two-tier per-key consumer ordering:
//       Tier 1 (cross-instance, "ordered-processing" endpoint): RabbitMQ SAC (x-single-active-consumer)
//              promotes exactly one active consumer across competing replicas → ordered delivery per key
//              cross-instance. OrderedBy ByHeader("ordering-key") + TransportAffinity(SingleActiveConsumer).
//       Tier 2 (single-instance, "local-partitioned-processing" endpoint): LocalPartitioned fixed-lane
//              hashing with typed selector (m => m.AccountId). Demonstrates M3 caveat: typed selector
//              is cross-instance-safe only under LocalPartitioned or when selector == routing key.
//   - Poison-head contract (C3): MaxDeliveryAttempts(2) on the SAC endpoint parks the poison head
//              via DLX after 2 delivery attempts, then releases the key stream (seq 1..N delivered).
//
// Two-tier model:
//   Tier 1 (SAC, cross-instance):
//     POST /events/generate  →  Outbox PerKey  →  ordered-processing (SAC queue)
//                                                     └→ OrderShippedConsumer
//                                                     └→ InventoryAdjustedConsumer
//   Tier 2 (LocalPartitioned, single-instance):
//     POST /events/generate  →  (same outbox)   →  local-partitioned-processing (standard queue)
//                                                     └→ OrderShippedConsumer (typed selector m.AccountId)
//
// POST /events/generate?withPoison=true injects a synthetic poison key (seq=0 throws, seq 1..4 resume).
// SEC: the poison key value is generated internally; it NEVER appears in query strings (SEC-3).
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   - PostgreSQL server (default: Host=localhost;Database=barewiredb;Username=postgres;Password=postgres)
//   When running via Aspire AppHost, both are provisioned automatically.

using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire;
using BareWire.Transport.RabbitMQ;
using BareWire.Outbox.EntityFramework;
using BareWire.Samples.OrderedConsumers.Consumers;
using BareWire.Samples.OrderedConsumers.Data;
using BareWire.Samples.OrderedConsumers.Messages;
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
// 3. EF Core — application DbContext for processed-record log
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddDbContext<OrderedConsumersDbContext>(o => o.UseNpgsql(dbConnectionString));

// No singleton PoisonKeyHolder needed — the poison-head indicator is stamped as a
// transport header ("poison-head-demo: true") on seq=0 of the poison key only. This
// approach works correctly across all replicas without shared in-process state.

// ─────────────────────────────────────────────────────────────────────────────
// 5. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
builder.Services.AddBareWireJsonSerializer();

// Register consumers in DI (resolved per-message by ConsumerDispatcher, transient lifetime).
// OrderShippedConsumer handles the SAC endpoint (ordered-processing).
// LocalPartitionedOrderShippedConsumer handles the LocalPartitioned endpoint.
// InventoryAdjustedConsumer handles the SAC endpoint for InventoryAdjusted messages.
builder.Services.AddTransient<OrderShippedConsumer>();
builder.Services.AddTransient<LocalPartitionedOrderShippedConsumer>();
builder.Services.AddTransient<InventoryAdjustedConsumer>();

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    // Connection to the RabbitMQ broker.
    rmq.Host(rabbitMqConnectionString);
    rmq.DefaultExchange("ordered-events");

    // ADR-002: Manual topology — declare all exchanges, queues, and bindings explicitly.
    rmq.ConfigureTopology(t =>
    {
        // Topic exchange for all ordered events.
        t.DeclareExchange("ordered-events", ExchangeType.Topic, durable: true);

        // Dead-letter exchange + queue for parked poison heads (C3 contract).
        t.DeclareExchange("ordered-events-dlx", ExchangeType.Direct, durable: true);
        t.DeclareQueue("ordered-events-dlq", durable: true);
        t.BindExchangeToQueue("ordered-events-dlx", "ordered-events-dlq",
            routingKey: "ordered-events-dlq");

        // Tier 1: SAC queue — x-single-active-consumer promotes exactly one active consumer
        // across replicas at any moment. DLX wired for poison-head parking after MaxDeliveryAttempts.
        t.DeclareQueue("ordered-processing", durable: true, autoDelete: false,
            configure: q => q
                .SingleActiveConsumer()
                .DeadLetterExchange("ordered-events-dlx")
                .DeadLetterRoutingKey("ordered-events-dlq"));
        t.BindExchangeToQueue("ordered-events", "ordered-processing", routingKey: "#");

        // Tier 2: Standard queue for LocalPartitioned variant (single-process, fixed-lane hashing).
        t.DeclareQueue("local-partitioned-processing", durable: true);
        t.BindExchangeToQueue("ordered-events", "local-partitioned-processing", routingKey: "#");
    });

    // ── Tier 1: SAC cross-instance endpoint ──────────────────────────────────
    // ADR-026 §4: ByHeader("ordering-key") is symmetric to the producer-side outbox header.
    // TransportAffinity(SingleActiveConsumer) is a store-only annotation that drives fail-fast
    // if the queue is not declared with x-single-active-consumer (GAP-2 mitigation).
    // MaxDeliveryAttempts(2): after 2 failed deliveries the poison head is DLX-parked and the
    // key stream resumes for subsequent messages (C3 / poison-head contract).
    rmq.ReceiveEndpoint("ordered-processing", e =>
    {
        e.ConcurrentMessageLimit = 16;
        e.OrderedBy(o =>
        {
            o.ByHeader("ordering-key");
            o.TransportAffinity(TransportAffinity.SingleActiveConsumer);
            o.MaxDeliveryAttempts(2);
        });
        e.Consumer<OrderShippedConsumer, OrderShipped>();
        e.Consumer<InventoryAdjustedConsumer, InventoryAdjusted>();
    });

    // ── Tier 2: LocalPartitioned typed-selector endpoint ─────────────────────
    // ADR-026 §4 / M3 caveat: a typed selector (m => m.AccountId) reads a CLR property after
    // deserialization, which is cross-instance-safe ONLY under LocalPartitioned (or when the
    // selector equals the routing key). This endpoint explicitly declares LocalPartitioned so the
    // fixed-lane hashing is scoped to this single process instance.
    // Concurrency(8): 8 fixed lanes; messages with different AccountIds run fully in parallel
    // within the lane set; messages sharing an AccountId are serialized on their lane.
    // LocalPartitionedOrderShippedConsumer tags records with endpoint "local-partitioned-processing"
    // so that smoke-test ordering assertions can isolate SAC records (cross-instance guarantee).
    rmq.ReceiveEndpoint("local-partitioned-processing", e =>
    {
        e.ConcurrentMessageLimit = 8;
        e.OrderedBy(o => o
            .By<OrderShipped>(m => m.AccountId)
            .Strategy(ConsumerOrderingStrategy.LocalPartitioned)
            .Concurrency(8));
        e.Consumer<LocalPartitionedOrderShippedConsumer, OrderShipped>();
    });
};

builder.Services.AddBareWireRabbitMq(configureRabbitMq);
// UseRabbitMQ is a deprecated no-op (Feature 15, ADR-028 D4); transport comes from AddBareWireRabbitMq.
// Migration to AddBareWireWithRabbitMq is task 15.11; CS0618 suppressed here to keep the build green.
#pragma warning disable CS0618 // Type or member is obsolete
builder.Services.AddBareWire(cfg => cfg.UseRabbitMQ(configureRabbitMq));
#pragma warning restore CS0618 // Type or member is obsolete

// ─────────────────────────────────────────────────────────────────────────────
// 6. Transactional outbox — PerKey ordering (ADR-025)
// ─────────────────────────────────────────────────────────────────────────────

// Registers OutboxDbContext (separate DbContext sharing the same connection string),
// OutboxDispatcher (background dispatcher), OutboxCleanupService, and TransactionalOutboxMiddleware.
// OrderingMode.PerKey: the outbox promotes the "ordering-key" header to its ordering column and
// guarantees head-of-line ordering per key group at dispatch time, closing the produce → consume loop.
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options.UseNpgsql(dbConnectionString),
    configureOutbox: outbox =>
    {
        outbox.PollingInterval = TimeSpan.FromMilliseconds(500);
        outbox.DispatchBatchSize = 100;
        outbox.AutoCreateSchema = true;
        outbox.OrderingMode = BareWire.Abstractions.Outbox.OrderingMode.PerKey;
        outbox.OrderingKeyHeaderName = "ordering-key";
    });

// ─────────────────────────────────────────────────────────────────────────────
// 7. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// Development only — idempotent schema init for the application DbContext.
// Multiple replicas starting concurrently may race on DDL; catch the full set of
// Postgres concurrent-DDL errors: 42P07 (table exists), 40P01 (deadlock), 23505
// (unique violation from concurrent index creation), 25006 (in-failed-transaction).
// PERF-4 mitigation: broadened catch avoids spurious startup failures under replica races.
using (IServiceScope scope = app.Services.CreateScope())
{
    OrderedConsumersDbContext db =
        scope.ServiceProvider.GetRequiredService<OrderedConsumersDbContext>();
    await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
    try
    {
        IRelationalDatabaseCreator creator =
            db.Database.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>();
        await creator.CreateTablesAsync().ConfigureAwait(false);
    }
    catch (Npgsql.PostgresException ex)
        when (ex.SqlState is "42P07" or "40P01" or "23505" or "25006")
    {
        // Tables already exist or concurrent DDL raced — safe to ignore in development.
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 8. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoints: /health, /health/live, /health/ready.
app.MapServiceDefaults();

// POST /events/generate — publishes a burst of ordered events via the transactional outbox.
//
// Healthy scenario: 3 accounts (acct-A, acct-B, acct-C) × 5 sequences each.
// Poison scenario (withPoison=true): adds 1 synthetic poison account × 5 sequences;
//   seq=0 for the poison key throws in OrderShippedConsumer → MaxDeliveryAttempts(2) → DLX parking;
//   seq 1..4 resume after the head is parked, demonstrating the key-release contract.
//
// Returns a RunId in the response. The smoke test passes this RunId to GET /events/processing-log
// as a query parameter to isolate records from this run (avoids stale data from prior runs
// accumulating on the shared DB). RunId is stamped on each message as the "run-id" header.
//
// SEC-3: the poison key value is generated internally (high-entropy Guid fragment); it NEVER
// appears in the query string. The boolean flag ?withPoison=true is the only observable parameter.
// SEC-1: the consumer throws a constant exception string with NO key interpolation.
app.MapPost("/events/generate", async (
    IPublishEndpoint bus,
    OrderedConsumersDbContext dbContext,
    bool withPoison,
    CancellationToken cancellationToken) =>
{
    // Synthetic, non-PII demo keys (SEC-1: these are not personal data).
    string[] healthyKeys = ["acct-A", "acct-B", "acct-C"];
    const int sequencesPerKey = 5;

    // Generate a unique run identifier so the smoke test can isolate this run's records.
    string runId = Guid.NewGuid().ToString("N");

    DateTime now = DateTime.UtcNow;
    int published = 0;

    // Publish healthy keys — alternating OrderShipped and InventoryAdjusted per sequence.
    foreach (string key in healthyKeys)
    {
        for (int seq = 0; seq < sequencesPerKey; seq++)
        {
            var headers = new Dictionary<string, string>
            {
                ["ordering-key"] = key,
                ["run-id"] = runId,
            };

            if (seq % 2 == 0)
            {
                await bus.PublishAsync(
                    new OrderShipped(
                        OrderingKey: key,
                        AccountId: key,
                        Sequence: seq,
                        ShipmentId: $"SHP-{key}-{seq:D2}",
                        OccurredAt: now),
                    headers, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await bus.PublishAsync(
                    new InventoryAdjusted(
                        OrderingKey: key,
                        AccountId: key,
                        Sequence: seq,
                        AdjustmentId: $"ADJ-{key}-{seq:D2}",
                        OccurredAt: now),
                    headers, cancellationToken).ConfigureAwait(false);
            }

            published++;
        }
    }

    // SEC-3: generate the poison key internally; its value NEVER appears in query strings,
    // logs (via log-parameter safety), or exception messages (OrderShippedConsumer throws constant).
    // The poison-head indicator is stamped as "poison-head-demo: true" on seq=0 only, so the
    // consumer can detect it on ANY replica without shared in-process state (PoisonKeyHolder removed).
    string? poisonKey = null;
    if (withPoison)
    {
        // Use a high-entropy Guid fragment as the poison key sentinel.
        // The smoke test asserts this value is ABSENT from HTTP response bodies (SEC-2).
        poisonKey = $"poison-{Guid.NewGuid():N}";

        for (int seq = 0; seq < sequencesPerKey; seq++)
        {
            var headers = new Dictionary<string, string>
            {
                ["ordering-key"] = poisonKey,
                ["run-id"] = runId,
            };

            // Stamp the poison-head-demo header ONLY on seq=0 — the consumer throws for this one.
            // Subsequent messages (seq 1..4) have no such header → processed normally after parking.
            if (seq == 0)
            {
                headers["poison-head-demo"] = "true";
            }

            await bus.PublishAsync(
                new OrderShipped(
                    OrderingKey: poisonKey,
                    AccountId: poisonKey,
                    Sequence: seq,
                    ShipmentId: $"SHP-poison-{seq:D2}",
                    OccurredAt: now),
                headers, cancellationToken).ConfigureAwait(false);

            published++;
        }
    }

    // Commit the outbox entries atomically with the DB context save.
    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

    return Results.Accepted(value: new
    {
        Published = published,
        RunId = runId,
        HealthyKeys = healthyKeys,
        PoisonKeyInjected = withPoison,
        // SEC: the raw poison key value is deliberately NOT returned in the response body.
    });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("GenerateEvents");

// GET /events/processing-log — returns ProcessedRecord rows ordered by Id.
// Optional ?runId= filter to isolate records from a single POST /events/generate call.
// Used by the smoke test to verify strict per-key ordering across competing replicas.
// SEC-1: key values in this endpoint are synthetic non-PII demo identifiers (acct-A, acct-B, acct-C).
// The poison sentinel value is absent — parked heads are never recorded as ProcessedRecords.
app.MapGet("/events/processing-log", async (
    OrderedConsumersDbContext dbContext,
    string? runId,
    CancellationToken cancellationToken) =>
{
    IQueryable<ProcessedRecord> query = dbContext.ProcessedRecords.OrderBy(r => r.Id);
    if (!string.IsNullOrEmpty(runId))
    {
        query = query.Where(r => r.RunId == runId);
    }

    List<ProcessedRecord> records = await query
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(records);
})
.Produces<List<ProcessedRecord>>()
.WithName("GetProcessingLog");

app.Run();
