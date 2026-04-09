// BareWire.Samples.MassTransitInterop — demonstrates coexistence of BareWire and a MassTransit
// producer on a shared RabbitMQ broker, with transparent envelope unwrapping and per-endpoint
// serializer override for publishing in MassTransit envelope format.
//
// What this sample shows:
//   - ADR-001  Raw-first: /barewire/publish sends plain JSON; no envelope by default.
//   - ADR-002  Manual topology: exchanges, queues, and bindings declared explicitly.
//   - ADR-005  MassTransit naming: IBus, IConsumer<T>, ConsumeContext<T>.
//   - MassTransit interop (receive): AddMassTransitEnvelopeDeserializer() registers a
//     content-type-aware deserializer for application/vnd.masstransit+json. BareWire's
//     ContentTypeDeserializerRouter unwraps the envelope transparently — MassTransitOrderConsumer
//     receives a plain OrderCreated record, identical to what BareWireOrderConsumer receives.
//   - MassTransit interop (publish): AddMassTransitEnvelopeSerializer() registers
//     MassTransitEnvelopeSerializer in DI for per-endpoint use. UseSerializer<T>() on a receive
//     endpoint overrides the serializer for that endpoint only — published messages from consumers
//     on that endpoint are wrapped in a MassTransit envelope. The default raw JSON serializer
//     (ADR-001) is not affected.
//   - MassTransitSimulator: BackgroundService using bare RabbitMQ.Client (no BareWire) to
//     simulate a MassTransit producer publishing enveloped messages every 5 seconds.
//   - ServiceDefaults: OpenTelemetry observability + health checks.
//
// Architecture:
//   MassTransitSimulator (RabbitMQ.Client) → mt-orders (direct exchange)
//       └→ mt-orders-queue → MassTransitOrderConsumer (IConsumer<OrderCreated>)
//            Content-Type: application/vnd.masstransit+json
//            → ContentTypeDeserializerRouter → MassTransitEnvelopeDeserializer → OrderCreated
//
//   IBus.PublishAsync<OrderCreated>() → barewire-orders (direct exchange)
//       └→ barewire-orders-queue → BareWireOrderConsumer (IConsumer<OrderCreated>)
//            Content-Type: application/json
//            → ContentTypeDeserializerRouter → SystemTextJsonRawDeserializer → OrderCreated
//
//   IBus.PublishAsync<OrderCreated>() [UseSerializer<MassTransitEnvelopeSerializer>()]
//       → mt-envelope-publish (direct exchange)
//       └→ mt-envelope-publish-queue → MassTransitOrderConsumer (IConsumer<OrderCreated>)
//            Content-Type: application/vnd.masstransit+json (per-endpoint override)
//            → ContentTypeDeserializerRouter → MassTransitEnvelopeDeserializer → OrderCreated
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   When running via Aspire AppHost, the broker is provisioned automatically.

using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire;
using BareWire.Transport.RabbitMQ;
using BareWire.Serialization.Json;
using BareWire.Interop.MassTransit;
using BareWire.Samples.MassTransitInterop.Consumers;
using BareWire.Samples.MassTransitInterop.Messages;
using BareWire.Samples.MassTransitInterop.Services;
using BareWire.Samples.ServiceDefaults;

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

// ─────────────────────────────────────────────────────────────────────────────
// 3. BareWire serialization — ADR-001 raw-first + MassTransit envelope interop
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
// No envelope wrapper added by default.
builder.Services.AddBareWireJsonSerializer();

// MassTransit interop — registers MassTransitEnvelopeDeserializer and wires it into
// ContentTypeDeserializerRouter for application/vnd.masstransit+json.
// IMPORTANT: must be called AFTER AddBareWireJsonSerializer() — requires the router
// to be registered first; calling before throws InvalidOperationException.
builder.Services.AddMassTransitEnvelopeDeserializer();

// Registers MassTransitEnvelopeSerializer in DI for per-endpoint use.
// Does NOT replace the default raw JSON serializer (ADR-001 raw-first).
// Activate per endpoint via e.UseSerializer<MassTransitEnvelopeSerializer>().
builder.Services.AddMassTransitEnvelopeSerializer();

// ─────────────────────────────────────────────────────────────────────────────
// 4. Consumers
// ─────────────────────────────────────────────────────────────────────────────

// Register consumers in DI (resolved per-message by ConsumerDispatcher).
builder.Services.AddTransient<MassTransitOrderConsumer>();
builder.Services.AddTransient<BareWireOrderConsumer>();

// In-memory store for tracking processed orders (used by GET /orders/processed for E2E testing).
builder.Services.AddSingleton<ProcessedOrderStore>();

// ─────────────────────────────────────────────────────────────────────────────
// 5. BareWire messaging — transport, topology, receive endpoints
// ─────────────────────────────────────────────────────────────────────────────

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    // Connection to the RabbitMQ broker.
    rmq.Host(rabbitMqConnectionString);
    rmq.DefaultExchange("barewire-orders");

    // ADR-002: Manual topology — declare all exchanges, queues, and bindings explicitly.
    // The broker resources are deployed by IBusControl.DeployTopologyAsync on startup.
    rmq.ConfigureTopology(t =>
    {
        // Direct exchange for MassTransit-format messages (published by MassTransitSimulator).
        t.DeclareExchange("mt-orders", ExchangeType.Direct, durable: true);
        t.DeclareQueue("mt-orders-queue", durable: true);
        t.BindExchangeToQueue("mt-orders", "mt-orders-queue", routingKey: "");

        // Fanout exchange for raw JSON messages (published by IBus.PublishAsync).
        // Fanout ignores routing keys — IBus.PublishAsync resolves the routing key from the
        // message type name, so Direct would require a matching binding key.
        t.DeclareExchange("barewire-orders", ExchangeType.Fanout, durable: true);
        t.DeclareQueue("barewire-orders-queue", durable: true);
        t.BindExchangeToQueue("barewire-orders", "barewire-orders-queue", routingKey: "");

        // Exchange + queue for messages published by BareWire in MassTransit envelope format
        // (per-endpoint serializer override via UseSerializer<MassTransitEnvelopeSerializer>()).
        t.DeclareExchange("mt-envelope-publish", ExchangeType.Direct, durable: true);
        t.DeclareQueue("mt-envelope-publish-queue", durable: true);
        t.BindExchangeToQueue("mt-envelope-publish", "mt-envelope-publish-queue", routingKey: "");

        // Exchange for the publish-only bridge demo (no receive endpoint — bus.MapSerializer<T,S>() only).
        // Fanout so any downstream MassTransit consumer bound to this exchange can receive the envelope.
        t.DeclareExchange("mt-bridge-orders", ExchangeType.Fanout, durable: true);
    });

    // Endpoint 1: MassTransitOrderConsumer — receives enveloped messages from MassTransitSimulator.
    // ContentTypeDeserializerRouter detects application/vnd.masstransit+json and unwraps the
    // envelope before the consumer is invoked.
    rmq.ReceiveEndpoint("mt-orders-queue", e =>
    {
        e.PrefetchCount = 8;
        e.ConcurrentMessageLimit = 4;
        e.RetryCount = 3;
        e.RetryInterval = TimeSpan.FromSeconds(5);
        e.Consumer<MassTransitOrderConsumer, OrderCreated>();
    });

    // Endpoint 2: BareWireOrderConsumer — receives raw JSON published by IBus.PublishAsync.
    // SystemTextJsonRawDeserializer handles application/json directly.
    rmq.ReceiveEndpoint("barewire-orders-queue", e =>
    {
        e.PrefetchCount = 8;
        e.ConcurrentMessageLimit = 4;
        e.RetryCount = 3;
        e.RetryInterval = TimeSpan.FromSeconds(5);
        e.Consumer<BareWireOrderConsumer, OrderCreated>();
    });

    // Endpoint 3: receives messages published by BareWire in MassTransit envelope format.
    // UseSerializer overrides the serializer for this endpoint only — published messages
    // from consumers on this endpoint will use MassTransit envelope format.
    rmq.ReceiveEndpoint("mt-envelope-publish-queue", e =>
    {
        e.PrefetchCount = 8;
        e.ConcurrentMessageLimit = 4;
        e.RetryCount = 3;
        e.RetryInterval = TimeSpan.FromSeconds(5);
        e.UseSerializer<MassTransitEnvelopeSerializer>();
        e.Consumer<MassTransitOrderConsumer, OrderCreated>();
    });
};

builder.Services.AddBareWireRabbitMq(configureRabbitMq);
builder.Services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(configureRabbitMq);

    // Publish-only bridge: BridgeOrderCreated published via IBus.PublishAsync will be serialized
    // as application/vnd.masstransit+json. A dedicated message type (not OrderCreated) is used so
    // the mapping does not affect the raw-JSON /barewire/publish demo. The publish call must also
    // target the mt-bridge-orders exchange explicitly via the BW-Exchange header — see the
    // /masstransit/bridge-publish endpoint below.
    // No ReceiveEndpoint is declared for mt-bridge-orders — this is a one-way outbound bridge.
    cfg.MapSerializer<BridgeOrderCreated, MassTransitEnvelopeSerializer>();
});

// ─────────────────────────────────────────────────────────────────────────────
// 6. MassTransit simulator — simulates an external MassTransit producer
// ─────────────────────────────────────────────────────────────────────────────

// Register as singleton so the HTTP endpoint can resolve and trigger it on demand,
// then register as a hosted service backed by the same instance.
builder.Services.AddSingleton<MassTransitSimulator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MassTransitSimulator>());

// ─────────────────────────────────────────────────────────────────────────────
// 7. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// ─────────────────────────────────────────────────────────────────────────────
// 8. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoints: /health, /health/live, /health/ready.
app.MapServiceDefaults();

// POST /masstransit/simulate — triggers an immediate one-off publish from MassTransitSimulator.
// Publishes a MassTransit-envelope message to mt-orders exchange; MassTransitOrderConsumer
// will receive and log the unwrapped OrderCreated record.
app.MapPost("/masstransit/simulate", async (
    MassTransitSimulator simulator,
    CancellationToken cancellationToken) =>
{
    await simulator.PublishOnceAsync(cancellationToken).ConfigureAwait(false);

    return Results.Accepted(value: new { Message = "MassTransit envelope published to mt-orders exchange." });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("SimulateMassTransitPublish");

// POST /barewire/publish — publishes a raw JSON OrderCreated via IBus.PublishAsync.
// BareWireOrderConsumer will receive and log the plain OrderCreated record (ADR-001 raw-first).
app.MapPost("/barewire/publish", async (
    IBus bus,
    CancellationToken cancellationToken) =>
{
    OrderCreated order = new(
        OrderId: Guid.NewGuid().ToString(),
        Amount: 99.99m,
        Currency: "PLN");

    await bus.PublishAsync(order, cancellationToken).ConfigureAwait(false);

    return Results.Accepted(value: new { Message = "Raw JSON OrderCreated published to barewire-orders exchange.", order.OrderId });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("PublishBareWireOrder");

// POST /masstransit/publish-envelope — publishes an OrderCreated via IBus using MassTransit
// envelope serialization (per-endpoint override via UseSerializer<MassTransitEnvelopeSerializer>()).
app.MapPost("/masstransit/publish-envelope", async (
    IBus bus,
    CancellationToken cancellationToken) =>
{
    OrderCreated order = new(
        OrderId: Guid.NewGuid().ToString(),
        Amount: 149.99m,
        Currency: "EUR");

    await bus.PublishAsync(order, cancellationToken).ConfigureAwait(false);

    return Results.Accepted(value: new { Message = "MT envelope OrderCreated published.", order.OrderId });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("PublishMassTransitEnvelope");

// POST /masstransit/bridge-publish — publish-only bridge demo.
// Demonstrates IBus.MapSerializer<OrderCreated, MassTransitEnvelopeSerializer>() without any
// ReceiveEndpoint. The message is serialized as application/vnd.masstransit+json and published to
// the mt-bridge-orders fanout exchange. No BareWire consumer subscribes to that exchange — this
// is a one-way outbound bridge for scenarios where BareWire forwards events to a MassTransit cluster.
//
// Contrast with /masstransit/publish-envelope (above), which uses a per-endpoint UseSerializer<T>()
// override that requires a ReceiveEndpoint for the consumer that triggers the publish.
app.MapPost("/masstransit/bridge-publish", async (
    IBus bus,
    CancellationToken cancellationToken) =>
{
    BridgeOrderCreated order = new(
        OrderId: Guid.NewGuid().ToString(),
        Amount: 75.00m,
        Currency: "USD");

    // MapSerializer<BridgeOrderCreated, MassTransitEnvelopeSerializer>() (registered in AddBareWire
    // above) causes this publish to use the MassTransit envelope format. The BW-Exchange header
    // overrides the DefaultExchange so the message lands on mt-bridge-orders instead of the
    // raw-JSON barewire-orders exchange.
    var headers = new Dictionary<string, string>
    {
        ["BW-Exchange"] = "mt-bridge-orders",
    };

    await bus.PublishAsync(order, headers, cancellationToken).ConfigureAwait(false);

    return Results.Accepted(value: new
    {
        Message = "BridgeOrderCreated published via publish-only bridge (application/vnd.masstransit+json) to mt-bridge-orders exchange.",
        order.OrderId,
    });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("BridgePublishOrder");

// GET /orders/processed — returns all orders processed by both consumers (MassTransit + BareWire).
// Used by E2E tests to verify that messages were consumed and deserialized correctly.
app.MapGet("/orders/processed", (ProcessedOrderStore store) =>
    Results.Ok(store.GetAll()))
    .Produces<IReadOnlyList<ProcessedOrder>>(StatusCodes.Status200OK)
    .WithName("GetProcessedOrders");

app.Run();
