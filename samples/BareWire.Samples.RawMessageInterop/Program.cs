// BareWire.Samples.RawMessageInterop — demonstrates IRawConsumer, custom header mapping,
// and interoperability with a legacy system that publishes raw JSON.
//
// What this sample shows:
//   - ADR-001  Raw-first: plain JSON, no BareWire envelope.
//   - ADR-002  Manual topology: exchanges, queues, and bindings declared explicitly.
//   - IRawConsumer: manual deserialization via TryDeserialize<T>() + custom header extraction.
//   - IConsumer<T>: automatic deserialization for the same message type on a separate queue.
//   - ConfigureHeaderMapping: maps X-Correlation-Id / X-Message-Type / X-Source-System to
//     BareWire canonical headers before consumers see them.
//   - LegacyPublisher: BackgroundService using bare RabbitMQ.Client (no BareWire) to simulate
//     a legacy system publishing plain JSON with custom headers.
//   - EF Core with PostgreSQL (Npgsql) for persisting processed messages.
//   - ServiceDefaults: OpenTelemetry observability + health checks.
//
// Architecture:
//   LegacyPublisher (RabbitMQ.Client) → legacy.events (fanout exchange)
//       ├→ raw-events  queue → RawEventConsumer  (IRawConsumer)     → PostgreSQL
//       └→ typed-events queue → TypedEventConsumer (IConsumer<T>)   → PostgreSQL
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   - PostgreSQL server (default: Host=localhost;Database=barewiredb;Username=postgres;Password=postgres)
//   When running via Aspire AppHost, both are provisioned automatically.

using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Core;
using BareWire.Transport.RabbitMQ;
using BareWire.Samples.RawMessageInterop.Consumers;
using BareWire.Samples.RawMessageInterop.Data;
using BareWire.Samples.RawMessageInterop.Messages;
using BareWire.Samples.RawMessageInterop.Services;
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
// 3. EF Core — PostgreSQL persistence for processed interop messages
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddDbContext<InteropDbContext>(o => o.UseNpgsql(dbConnectionString));

// ─────────────────────────────────────────────────────────────────────────────
// 4. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
// No envelope wrapper added by default.
builder.Services.AddBareWireJsonSerializer();

// Register consumers in DI (resolved per-message by ConsumerDispatcher).
builder.Services.AddTransient<RawEventConsumer>();
builder.Services.AddTransient<TypedEventConsumer>();

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    // Connection to the RabbitMQ broker.
    rmq.Host(rabbitMqConnectionString);
    rmq.DefaultExchange("legacy.events");

    // Custom header mapping — translate legacy system headers to BareWire canonical names.
    // LegacyPublisher sets these headers; both consumers read the canonical mapped versions.
    rmq.ConfigureHeaderMapping(headers =>
    {
        headers.MapCorrelationId("X-Correlation-Id");
        headers.MapMessageType("X-Message-Type");
        headers.MapHeader("SourceSystem", "X-Source-System");
    });

    // ADR-002: Manual topology — declare all exchanges, queues, and bindings explicitly.
    // The broker resources are deployed by IBusControl.DeployTopologyAsync on startup.
    rmq.ConfigureTopology(t =>
    {
        // Fanout exchange — every message from LegacyPublisher is fanned out to both queues.
        t.DeclareExchange("legacy.events", ExchangeType.Fanout, durable: true);

        // raw-events: consumed by RawEventConsumer (IRawConsumer — manual deserialization).
        t.DeclareQueue("raw-events", durable: true);
        t.BindExchangeToQueue("legacy.events", "raw-events", routingKey: "#");

        // typed-events: consumed by TypedEventConsumer (IConsumer<ExternalEvent> — auto deserialization).
        t.DeclareQueue("typed-events", durable: true);
        t.BindExchangeToQueue("legacy.events", "typed-events", routingKey: "#");
    });

    // Endpoint: RawEventConsumer receives undeserialized bytes and manually calls TryDeserialize<T>.
    rmq.ReceiveEndpoint("raw-events", e =>
    {
        e.PrefetchCount = 8;
        e.ConcurrentMessageLimit = 4;
        e.RetryCount = 3;
        e.RetryInterval = TimeSpan.FromSeconds(5);
        e.RawConsumer<RawEventConsumer>();
    });

    // Endpoint: TypedEventConsumer receives a fully-deserialized ExternalEvent.
    rmq.ReceiveEndpoint("typed-events", e =>
    {
        e.PrefetchCount = 8;
        e.ConcurrentMessageLimit = 4;
        e.RetryCount = 3;
        e.RetryInterval = TimeSpan.FromSeconds(5);
        e.Consumer<TypedEventConsumer, ExternalEvent>();
    });
};

builder.Services.AddBareWireRabbitMq(configureRabbitMq);
builder.Services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(configureRabbitMq);
});

// ─────────────────────────────────────────────────────────────────────────────
// 5. Legacy publisher — simulates an external system using bare RabbitMQ.Client
// ─────────────────────────────────────────────────────────────────────────────

// Register as singleton so the HTTP endpoint can resolve and trigger it on demand,
// then register as a hosted service backed by the same instance.
builder.Services.AddSingleton<LegacyPublisher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LegacyPublisher>());

// ─────────────────────────────────────────────────────────────────────────────
// 6. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// Development only — use migrations in production.
// EnsureCreatedAsync is a no-op when the database already exists (e.g. created by another sample
// sharing the same connection string). CreateTablesAsync adds missing tables for this DbContext.
using (IServiceScope scope = app.Services.CreateScope())
{
    InteropDbContext db = scope.ServiceProvider.GetRequiredService<InteropDbContext>();
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
// 7. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoints: /health, /health/live, /health/ready.
app.MapServiceDefaults();

// POST /legacy/simulate — triggers an immediate one-off publish from LegacyPublisher.
// Both consumers (RawEventConsumer and TypedEventConsumer) will process the resulting message.
app.MapPost("/legacy/simulate", async (
    LegacyPublisher publisher,
    CancellationToken cancellationToken) =>
{
    await publisher.PublishOnceAsync(cancellationToken).ConfigureAwait(false);

    return Results.Accepted(value: new { Message = "Legacy event published to legacy.events exchange." });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("SimulateLegacyPublish");

// GET /messages — returns all processed messages from PostgreSQL, newest first.
app.MapGet("/messages", async (
    InteropDbContext db,
    CancellationToken cancellationToken) =>
{
    List<ProcessedMessage> messages = await db.ProcessedMessages
        .OrderByDescending(m => m.ProcessedAt)
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(messages);
})
.Produces<List<ProcessedMessage>>()
.WithName("GetProcessedMessages");

app.Run();
