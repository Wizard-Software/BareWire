// BareWire.Samples.BasicPublishConsume — demonstrates basic publish/consume with PostgreSQL.
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: exchange and queue declared explicitly.
//   - EF Core with PostgreSQL (Npgsql) for persisting received messages.
//   - ServiceDefaults: OpenTelemetry observability + health checks.
//   - Minimal API endpoints: POST /messages (publish), GET /messages (query from DB).
//
// Architecture:
//   POST /messages → MessageSent
//       └→ MessageConsumer (queue: "messages") → persists to PostgreSQL
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   - PostgreSQL server (default: Host=localhost;Database=barewiredb;Username=postgres;Password=postgres)
//   When running via Aspire AppHost, both are provisioned automatically.

using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Core;
using BareWire.Transport.RabbitMQ;
using BareWire.Samples.BasicPublishConsume.Consumers;
using BareWire.Samples.BasicPublishConsume.Data;
using BareWire.Samples.BasicPublishConsume.Messages;
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
// 3. EF Core — PostgreSQL persistence for received messages
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddDbContext<SampleDbContext>(o => o.UseNpgsql(dbConnectionString));

// ─────────────────────────────────────────────────────────────────────────────
// 4. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
// No envelope wrapper added by default.
builder.Services.AddBareWireJsonSerializer();

// Register the consumer in DI (resolved per-message by ConsumerDispatcher).
builder.Services.AddTransient<MessageConsumer>();

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    // Connection to the RabbitMQ broker.
    rmq.Host(rabbitMqConnectionString);
    rmq.DefaultExchange("messages.events");

    // ADR-002: Manual topology — declare all exchanges, queues, and bindings explicitly.
    // The broker resources are deployed by IBusControl.DeployTopologyAsync on startup.
    rmq.ConfigureTopology(t =>
    {
        // Fanout exchange — every published MessageSent is delivered to the "messages" queue.
        t.DeclareExchange("messages.events", ExchangeType.Fanout, durable: true);

        t.DeclareQueue("messages", durable: true);
        t.BindExchangeToQueue("messages.events", "messages", routingKey: "#");
    });

    // Endpoint: MessageConsumer logs and persists every MessageSent event.
    rmq.ReceiveEndpoint("messages", e =>
    {
        e.Consumer<MessageConsumer, MessageSent>();
    });
};

builder.Services.AddBareWireRabbitMq(configureRabbitMq);
builder.Services.AddBareWire(cfg =>
{
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
    SampleDbContext db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
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

// POST /messages — publish a MessageSent event to the bus.
// The MessageConsumer on the "messages" queue will receive and persist it.
app.MapPost("/messages", async (
    MessageRequest request,
    IPublishEndpoint bus,
    CancellationToken cancellationToken) =>
{
    MessageSent message = new(request.Content, DateTime.UtcNow);

    await bus.PublishAsync(message, cancellationToken).ConfigureAwait(false);

    return Results.Accepted(value: new { message.Content, message.SentAt });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("PublishMessage");

// GET /messages — query all received messages from PostgreSQL.
app.MapGet("/messages", async (
    SampleDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    List<ReceivedMessage> messages = await dbContext.ReceivedMessages
        .OrderByDescending(m => m.ReceivedAt)
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(messages);
})
.Produces<List<ReceivedMessage>>()
.WithName("GetMessages");

app.Run();

// ─────────────────────────────────────────────────────────────────────────────
// Supporting types
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /messages.</summary>
internal sealed record MessageRequest(string Content);
