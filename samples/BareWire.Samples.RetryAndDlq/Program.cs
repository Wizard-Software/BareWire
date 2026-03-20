// BareWire.Samples.RetryAndDlq — demonstrates retry strategies and Dead Letter Queue routing.
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: all exchanges, queues, and bindings declared explicitly.
//   - Retry policy: e.RetryCount = 3 with e.RetryInterval = 1 s between attempts.
//   - Dead Letter Queue via RabbitMQ native DLX (x-dead-letter-exchange).
//   - PaymentProcessor: throws PaymentDeclinedException 70% of the time (simulated gateway).
//   - DlqConsumer: receives dead-lettered messages and persists FailedPayment to PostgreSQL.
//   - ServiceDefaults: OpenTelemetry observability + health checks.
//
// Architecture:
//   POST /payments → ProcessPayment
//       └→ PaymentProcessor (queue: "payments", retries: 3 × 1 s)
//              └─ on success: ack
//              └─ on exhausted retries: broker dead-letters to "payments.dlx"
//                     └→ DlqConsumer (queue: "payments-dlq") → persists FailedPayment to PostgreSQL
//
// RabbitMQ DLX topology:
//   Exchange "payments"     (topic, durable)  ──→ Queue "payments"     (durable)
//   Exchange "payments.dlx" (fanout, durable) ──→ Queue "payments-dlq" (durable)
//
//   The "payments" queue is declared with the RabbitMQ queue argument:
//     x-dead-letter-exchange  = "payments.dlx"
//   This is passed directly via ITopologyConfigurator.DeclareQueue(arguments: ...)
//   and applied by the transport adapter during topology deployment.
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   - PostgreSQL server (default: Host=localhost;Database=barewiredb;Username=postgres;Password=postgres)
//   When running via Aspire AppHost, both are provisioned automatically.

using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Core;
using BareWire.Transport.RabbitMQ;
using BareWire.Samples.RetryAndDlq.Consumers;
using BareWire.Samples.RetryAndDlq.Data;
using BareWire.Samples.RetryAndDlq.Messages;
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
// 3. EF Core — PostgreSQL persistence for dead-lettered payments
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddDbContext<PaymentDbContext>(o => o.UseNpgsql(dbConnectionString));

// ─────────────────────────────────────────────────────────────────────────────
// 4. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
builder.Services.AddBareWireJsonSerializer();

// Register consumers in DI (resolved per-message by ConsumerDispatcher).
builder.Services.AddTransient<PaymentProcessor>();
builder.Services.AddTransient<DlqConsumer>();

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    // Connection to the RabbitMQ broker.
    rmq.Host(rabbitMqConnectionString);
    rmq.DefaultExchange("payments");

    // ADR-002: Manual topology — declare all exchanges, queues, and bindings explicitly.
    // The broker resources are deployed by IBusControl.DeployTopologyAsync on startup.
    rmq.ConfigureTopology(t =>
    {
        // Topic exchange for payment commands.
        // Messages published via IPublishEndpoint are routed here.
        t.DeclareExchange("payments", ExchangeType.Topic, durable: true);

        // Main processing queue — declared with x-dead-letter-exchange so that RabbitMQ
        // automatically routes rejected messages to "payments.dlx" after retry exhaustion.
        t.DeclareQueue("payments", durable: true, arguments:
            new Dictionary<string, object> { ["x-dead-letter-exchange"] = "payments.dlx" });
        t.BindExchangeToQueue("payments", "payments", routingKey: "#");

        // Fanout DLX exchange — all dead-lettered payments fan out to "payments-dlq".
        t.DeclareExchange("payments.dlx", ExchangeType.Fanout, durable: true);

        // Dead-letter queue — receives messages from "payments.dlx" after retry exhaustion.
        t.DeclareQueue("payments-dlq", durable: true);
        t.BindExchangeToQueue("payments.dlx", "payments-dlq", routingKey: "");
    });

    // Endpoint: PaymentProcessor with retry policy.
    // After 3 failed attempts the broker native DLX mechanism routes the message
    // from "payments" to "payments.dlx" → "payments-dlq".
    rmq.ReceiveEndpoint("payments", e =>
    {
        e.RetryCount = 3;
        e.RetryInterval = TimeSpan.FromSeconds(1);
        e.Consumer<PaymentProcessor, ProcessPayment>();
    });

    // Endpoint: DlqConsumer subscribes to the dead-letter queue.
    // No retry on DLQ — a failed payment is logged and persisted as-is.
    rmq.ReceiveEndpoint("payments-dlq", e =>
    {
        e.Consumer<DlqConsumer, ProcessPayment>();
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
    PaymentDbContext db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
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

// POST /payments — publish a ProcessPayment command to the bus.
// PaymentProcessor on the "payments" queue will receive it, fail ~70% of the time,
// retry up to 3 times, then the broker dead-letters it to "payments-dlq".
app.MapPost("/payments", async (
    PaymentRequest request,
    IPublishEndpoint bus,
    CancellationToken cancellationToken) =>
{
    string paymentId = Guid.NewGuid().ToString();

    ProcessPayment message = new(paymentId, request.Amount, request.Currency);

    await bus.PublishAsync(message, cancellationToken).ConfigureAwait(false);

    return Results.Accepted(value: new { PaymentId = paymentId, request.Amount, request.Currency });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("SubmitPayment");

// GET /payments/failed — query all failed payments that landed in the DLQ.
app.MapGet("/payments/failed", async (
    PaymentDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    List<FailedPayment> failed = await dbContext.FailedPayments
        .OrderByDescending(p => p.FailedAt)
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(failed);
})
.Produces<List<FailedPayment>>()
.WithName("GetFailedPayments");

app.Run();

// ─────────────────────────────────────────────────────────────────────────────
// Supporting types
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /payments.</summary>
internal sealed record PaymentRequest(decimal Amount, string Currency);
