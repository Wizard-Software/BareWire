// BareWire.Samples.SagaOrderFlow — demonstrates a full order lifecycle using SAGA state machine.
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: exchange and queues declared explicitly.
//   - ADR-004  Credit-based flow control (bounded channels, inflight tracking).
//   - SAGA state machine (OrderSagaStateMachine) with EF Core + PostgreSQL persistence.
//   - Compensable activities (ReserveStock → ChargePayment → CreateShipment) for rollback.
//   - Scheduled payment timeout (30 seconds) via BareWire scheduling.
//   - ServiceDefaults: OpenTelemetry observability + health checks.
//
// Architecture:
//   POST /orders → OrderCreated
//       └→ OrderSagaStateMachine (queue: "order-saga"):
//              Initial ──OrderCreated──→ Processing  (schedule 30s timeout)
//              Processing ──PaymentReceived──→ Shipping  (cancel timeout)
//              Shipping ──ShipmentDispatched──→ Completed (finalized)
//              Processing ──PaymentFailed──→ Compensating  (cancel timeout)
//              Processing ──PaymentTimeout──→ Compensating  (fired after 30s)
//              Compensating ──CompensationCompleted──→ Failed (finalized)
//
//   GET /orders/{id}/status → queries saga repository for current state
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   - PostgreSQL server (default: Host=localhost;Database=barewiredb;Username=postgres;Password=postgres)
//   When running via Aspire AppHost, both are provisioned automatically.

using BareWire.Abstractions;
using BareWire.Abstractions.Saga;
using BareWire.Core;
using BareWire.Saga.EntityFramework;
using BareWire.Samples.SagaOrderFlow.Consumers;
using BareWire.Samples.SagaOrderFlow.Data;
using BareWire.Samples.SagaOrderFlow.Messages;
using BareWire.Samples.SagaOrderFlow.Saga;
using BareWire.Samples.ServiceDefaults;
using BareWire.Serialization.Json;
using Microsoft.EntityFrameworkCore;

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
// 3. EF Core — application DbContext (saga state managed by SagaDbContext)
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddDbContext<SagaOrderDbContext>(o => o.UseNpgsql(dbConnectionString));

// ─────────────────────────────────────────────────────────────────────────────
// 4. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
builder.Services.AddBareWireJsonSerializer();

// Register consumers in DI (resolved per-message by ConsumerDispatcher).
builder.Services.AddTransient<CompensationConsumer>();

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
            // Fanout exchange — all order events (created, payment, shipment) route here.
            t.DeclareExchange("order.events", ExchangeType.Fanout, durable: true);

            // Queue for the SAGA state machine — drives the complete order lifecycle.
            t.DeclareQueue("order-saga", durable: true);
            t.BindExchangeToQueue("order.events", "order-saga", routingKey: "#");
        });

        // Endpoint: OrderSagaStateMachine correlates all order events and drives state transitions.
        rmq.ReceiveEndpoint("order-saga", e =>
        {
            e.PrefetchCount = 8;
            e.ConcurrentMessageLimit = 4;
            e.RetryCount = 3;
            e.RetryInterval = TimeSpan.FromSeconds(2);
            e.StateMachineSaga<OrderSagaStateMachine>();
        });
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// 5. SAGA persistence (EF Core + PostgreSQL)
// ─────────────────────────────────────────────────────────────────────────────

// Persists OrderSagaState to PostgreSQL via EfCoreSagaRepository<OrderSagaState>.
// Optimistic concurrency is enforced via the Version column.
builder.Services.AddBareWireSaga<OrderSagaState>(
    options => options.UseNpgsql(dbConnectionString));

// Register the state machine so the runtime can resolve it from DI.
builder.Services.AddSingleton<OrderSagaStateMachine>();

// ─────────────────────────────────────────────────────────────────────────────
// 6. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// Development only — EnsureCreated creates the schema when none exists.
// Use EF Core migrations in production.
using (IServiceScope scope = app.Services.CreateScope())
{
    SagaDbContext sagaDb = scope.ServiceProvider.GetRequiredService<SagaDbContext>();
    await sagaDb.Database.EnsureCreatedAsync().ConfigureAwait(false);
}

// ─────────────────────────────────────────────────────────────────────────────
// 7. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoints: /health, /health/live, /health/ready.
app.MapServiceDefaults();

// POST /orders — publish OrderCreated to start the SAGA.
// The OrderSagaStateMachine on the "order-saga" queue will pick it up,
// create a new saga instance, and schedule the payment timeout.
app.MapPost("/orders", async (
    OrderRequest request,
    IPublishEndpoint bus,
    CancellationToken cancellationToken) =>
{
    string orderId = Guid.NewGuid().ToString();

    await bus.PublishAsync(
        new OrderCreated(orderId, request.Amount, request.ShippingAddress),
        cancellationToken).ConfigureAwait(false);

    return Results.Accepted($"/orders/{orderId}/status", new { OrderId = orderId });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("CreateOrder");

// GET /orders/{id}/status — query current saga state for the given order ID.
// The ID must be a valid Guid (as produced by POST /orders).
app.MapGet("/orders/{id}/status", async (
    string id,
    ISagaRepository<OrderSagaState> repository,
    CancellationToken cancellationToken) =>
{
    if (!Guid.TryParse(id, out Guid correlationId))
    {
        return Results.BadRequest(new { Error = "Order ID must be a valid GUID." });
    }

    OrderSagaState? state = await repository
        .FindAsync(correlationId, cancellationToken)
        .ConfigureAwait(false);

    if (state is null)
    {
        return Results.NotFound(new { Error = $"No saga found for order '{id}'." });
    }

    return Results.Ok(new
    {
        state.OrderId,
        state.CurrentState,
        state.Amount,
        state.ShippingAddress,
        state.PaymentId,
        state.TrackingNumber,
        state.FailureReason,
        state.CreatedAt,
        state.UpdatedAt,
    });
})
.Produces<object>()
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status404NotFound)
.WithName("GetOrderStatus");

app.Run();

// ─────────────────────────────────────────────────────────────────────────────
// Supporting types
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /orders.</summary>
internal sealed record OrderRequest(decimal Amount, string ShippingAddress);
