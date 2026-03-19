// BareWire.Samples.RequestResponse — demonstrates the request-response pattern with IRequestClient<T>.
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: ConfigureConsumeTopology = false (default), topology declared explicitly.
//   - IRequestClient<T>: bus.CreateRequestClient<T>() + client.GetResponseAsync<TResponse>().
//   - IConsumer<T>: context.RespondAsync() routes the response back via the ReplyTo header.
//   - EF Core + PostgreSQL: validation history persisted by the consumer.
//   - OpenTelemetry observability via BareWire.Samples.ServiceDefaults.
//
// Architecture:
//   POST /validate-order → ValidateOrder
//       └→ OrderValidationConsumer (queue: "order-validation") → OrderValidationResult
//           └→ Response returned synchronously to the caller via IRequestClient<T>
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   - PostgreSQL database (default: localhost:5432, database: barewiredb)
//   When running via Aspire AppHost, connection strings are injected automatically.

using BareWire.Abstractions;
using BareWire.Core;
using BareWire.Observability;
using BareWire.Samples.RequestResponse.Consumers;
using BareWire.Samples.RequestResponse.Data;
using BareWire.Samples.RequestResponse.Messages;
using BareWire.Samples.ServiceDefaults;
using BareWire.Serialization.Json;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// 1. ServiceDefaults — OTel observability + health checks (shared across samples)
// ─────────────────────────────────────────────────────────────────────────────

builder.AddServiceDefaults();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Configuration — Aspire injects ConnectionStrings:* at runtime via WithReference().
//    appsettings.json provides localhost fallbacks for running without Aspire.
// ─────────────────────────────────────────────────────────────────────────────

string rabbitMqConnectionString =
    builder.Configuration.GetConnectionString("rabbitmq")
    ?? "amqp://guest:guest@localhost:5672/";

string dbConnectionString =
    builder.Configuration.GetConnectionString("barewiredb")
    ?? "Host=localhost;Port=5432;Database=barewiredb;Username=postgres;Password=postgres";

// ─────────────────────────────────────────────────────────────────────────────
// 3. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
// No envelope wrapper is added by default.
builder.Services.AddBareWireJsonSerializer();

// Register consumer in DI (resolved per-message by ConsumerDispatcher).
builder.Services.AddTransient<OrderValidationConsumer>();

builder.Services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(rmq =>
    {
        // Connection to the RabbitMQ broker.
        rmq.Host(rabbitMqConnectionString);

        // ADR-002: Manual topology — declare all exchanges, queues, and bindings explicitly.
        // The broker resources are deployed by IBusControl.DeployTopologyAsync on startup.
        rmq.ConfigureTopology(t =>
        {
            // Direct exchange: routes ValidateOrder commands to the single validation queue.
            t.DeclareExchange("order-validation", ExchangeType.Direct, durable: true);

            // Queue for OrderValidationConsumer.
            t.DeclareQueue("order-validation", durable: true);
            t.BindExchangeToQueue("order-validation", "order-validation", routingKey: "order-validation");
        });

        // Endpoint: OrderValidationConsumer processes ValidateOrder requests and responds
        // with OrderValidationResult via context.RespondAsync().
        rmq.ReceiveEndpoint("order-validation", e =>
        {
            e.PrefetchCount = 16;
            e.ConcurrentMessageLimit = 8;
            e.RetryCount = 3;
            e.RetryInterval = TimeSpan.FromSeconds(5);
            e.Consumer<OrderValidationConsumer, ValidateOrder>();
        });
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// 4. EF Core — PostgreSQL persistence for validation history
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddDbContext<ValidationDbContext>(options =>
    options.UseNpgsql(dbConnectionString));

// ─────────────────────────────────────────────────────────────────────────────
// 5. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// Development only — use migrations in production.
using (IServiceScope scope = app.Services.CreateScope())
{
    ValidationDbContext db = scope.ServiceProvider.GetRequiredService<ValidationDbContext>();
    await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// POST /validate-order — sends a ValidateOrder request and waits for OrderValidationResult.
// Demonstrates IRequestClient<T>: creates a transient request client, sends the command,
// and blocks until the consumer calls context.RespondAsync() with the result.
app.MapPost("/validate-order", async (
    ValidateOrderRequest request,
    IBus bus,
    CancellationToken cancellationToken) =>
{
    IRequestClient<ValidateOrder> client = bus.CreateRequestClient<ValidateOrder>();

    Response<OrderValidationResult> response = await client
        .GetResponseAsync<OrderValidationResult>(
            new ValidateOrder(request.OrderId, request.Amount),
            cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(response.Message);
})
.Produces<OrderValidationResult>()
.ProducesProblem(StatusCodes.Status408RequestTimeout)
.WithName("ValidateOrder")
.WithSummary("Validate an order via request-response messaging.");

// GET /validations — returns the full validation history from PostgreSQL.
app.MapGet("/validations", async (
    ValidationDbContext db,
    CancellationToken cancellationToken) =>
{
    List<ValidationRecord> records = await db.ValidationRecords
        .OrderByDescending(r => r.ValidatedAt)
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(records);
})
.Produces<List<ValidationRecord>>()
.WithName("GetValidations")
.WithSummary("Retrieve all order validation records from the database.");

// Map health check endpoints: /health, /health/live, /health/ready.
app.MapServiceDefaults();

app.Run();

// ─────────────────────────────────────────────────────────────────────────────
// Request model
// ─────────────────────────────────────────────────────────────────────────────

// Separate HTTP request model from the BareWire message record — keeps the message
// type clean (no HTTP concerns) and allows independent evolution of the API surface.
internal sealed record ValidateOrderRequest(string OrderId, decimal Amount);
