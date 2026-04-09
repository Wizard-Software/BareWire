# MassTransit Interop

BareWire can both consume and publish messages in MassTransit's envelope format without requiring MassTransit as a runtime dependency. The `BareWire.Interop.MassTransit` package provides a content-type-aware deserializer for consuming MassTransit messages and a per-endpoint serializer for publishing in MassTransit-compatible format.

## Installation

```bash
dotnet add package BareWire.Interop.MassTransit
```

## Configuration

Register the MassTransit interop components **after** the base JSON serializer:

```csharp
builder.Services.AddBareWireJsonSerializer();
builder.Services.AddMassTransitEnvelopeDeserializer(); // consume from MassTransit
builder.Services.AddMassTransitEnvelopeSerializer();   // publish to MassTransit (per-endpoint)
```

The order matters — both methods throw `InvalidOperationException` if called before `AddBareWireJsonSerializer()`.

`AddMassTransitEnvelopeSerializer()` registers the serializer in DI but does **not** replace the default raw JSON serializer. To activate it, use `UseSerializer<MassTransitEnvelopeSerializer>()` on the endpoints that need to publish in MassTransit format (see [Publishing to MassTransit](#publishing-to-masstransit) below).

## How It Works

MassTransit wraps every message in a JSON envelope with metadata fields:

```json
{
  "messageId": "550e8400-e29b-41d4-a716-446655440000",
  "correlationId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
  "conversationId": "...",
  "sourceAddress": "rabbitmq://cluster/order-service",
  "destinationAddress": "rabbitmq://cluster/payment-queue",
  "messageType": ["urn:message:MyNamespace:OrderCreated"],
  "sentTime": "2026-03-02T10:30:00Z",
  "headers": {},
  "message": { "orderId": "abc-123", "amount": 99.99 }
}
```

When BareWire receives a message with `Content-Type: application/vnd.masstransit+json`, the `ContentTypeDeserializerRouter` routes it to `MassTransitEnvelopeDeserializer`, which:

1. Parses the outer envelope
2. Extracts the `message` field
3. Deserializes it into the target type (e.g. `OrderCreated`)

Your consumer receives a plain `OrderCreated` record — identical to what it would receive from a raw BareWire publisher. No consumer code changes are needed.

## Coexistence on a Shared Broker

A single BareWire application can consume both MassTransit-envelope and raw JSON messages simultaneously. Each queue can carry a different format — the content-type header drives deserialization:

```csharp
// Topology: two independent flows on the same broker
rmq.ConfigureTopology(t =>
{
    t.DeclareExchange("mt-orders", ExchangeType.Direct, durable: true);
    t.DeclareQueue("mt-orders-queue", durable: true);
    t.BindExchangeToQueue("mt-orders", "mt-orders-queue", routingKey: "");

    t.DeclareExchange("bw-orders", ExchangeType.Fanout, durable: true);
    t.DeclareQueue("bw-orders-queue", durable: true);
    t.BindExchangeToQueue("bw-orders", "bw-orders-queue", routingKey: "");
});

// Both consumers implement IConsumer<OrderCreated> — same interface, different sources
rmq.ReceiveEndpoint("mt-orders-queue", e =>
{
    e.Consumer<MtOrderConsumer, OrderCreated>();
});

rmq.ReceiveEndpoint("bw-orders-queue", e =>
{
    e.Consumer<BwOrderConsumer, OrderCreated>();
});
```

No per-endpoint deserializer override is needed — the `ContentTypeDeserializerRouter` handles format selection automatically based on the `Content-Type` header of each message.

## Permissive Parsing

The envelope deserializer is intentionally permissive:

- All metadata fields (`messageId`, `correlationId`, `headers`, etc.) are optional — a minimal envelope with just a `message` field is valid
- Unknown fields (`host`, `faultAddress`, `requestId`) are silently ignored
- `null` or missing `message` field returns `null` (not an exception)
- Malformed JSON throws `BareWireSerializationException` with a raw payload excerpt for debugging

## Publishing to MassTransit

To publish messages that MassTransit consumers can understand, activate the `MassTransitEnvelopeSerializer` on the endpoint that communicates with MassTransit:

```csharp
rmq.ReceiveEndpoint("to-masstransit", e =>
{
    e.UseSerializer<MassTransitEnvelopeSerializer>();
    e.Consumer<OutboundConsumer, OrderCreated>();
});
```

Messages published from this endpoint are wrapped in a MassTransit-compatible envelope:

```json
{
  "messageId": "550e8400-e29b-41d4-a716-446655440000",
  "messageType": ["urn:message:MyNamespace:OrderCreated"],
  "sentTime": "2026-04-04T12:00:00Z",
  "message": { "orderId": "abc-123", "amount": 99.99, "currency": "PLN" }
}
```

Other endpoints continue using the default raw JSON serializer — `UseSerializer<T>()` only affects the endpoint it is called on.

### Bidirectional Interop

For a single endpoint that both receives and publishes in MassTransit format, combine both overrides:

```csharp
rmq.ReceiveEndpoint("masstransit-bridge", e =>
{
    e.UseSerializer<MassTransitEnvelopeSerializer>();
    e.UseDeserializer<MassTransitEnvelopeDeserializer>();
    e.Consumer<BridgeConsumer, OrderCreated>();
});
```

### Mixed Endpoints

A single application can have endpoints with different serialization formats:

```csharp
// Internal: raw JSON (default)
rmq.ReceiveEndpoint("internal-events", e =>
{
    e.Consumer<InternalConsumer, InternalEvent>();
});

// MassTransit bridge: envelope format
rmq.ReceiveEndpoint("masstransit-bridge", e =>
{
    e.UseSerializer<MassTransitEnvelopeSerializer>();
    e.UseDeserializer<MassTransitEnvelopeDeserializer>();
    e.Consumer<BridgeConsumer, OrderCreated>();
});
```

## Publish-only bridge (no receive endpoint)

The scenarios above require at least one `ReceiveEndpoint` to activate the per-endpoint serializer override. When your application only **publishes** to a MassTransit-compatible exchange and does not consume any MassTransit queues, you can use `IBusConfigurator.MapSerializer<TMessage, TSerializer>()` instead — no receive endpoint required.

This is the recommended pattern when BareWire acts as a bridge that forwards events to an existing MassTransit cluster without subscribing to any queues.

```csharp
// Register serializers first (order matters).
services.AddBareWireJsonSerializer();
services.AddMassTransitEnvelopeSerializer(); // registers MassTransitEnvelopeSerializer in DI

services.AddBareWireRabbitMq(rmq =>
{
    rmq.Host("amqp://guest:guest@localhost:5672/");
    rmq.ConfigureTopology(topo => topo.Exchange("mt-orders").Fanout().Durable());
    rmq.DefaultExchange("mt-orders");
    // No ReceiveEndpoint — publish-only bridge
});

services.AddBareWire(bus =>
{
    bus.MapSerializer<OrderCreated, MassTransitEnvelopeSerializer>();
    // All other message types continue using the default raw JSON serializer (ADR-001).
});

// Usage:
await bus.PublishAsync(new OrderCreated(...), ct);
// → Content-Type: application/vnd.masstransit+json

await bus.PublishAsync(new PaymentRequested(...), ct);
// → Content-Type: application/json (default, unaffected)
```

The mapping is bus-global and transport-agnostic: it applies to both `IBus.PublishAsync<T>()` and `ISendEndpoint.SendAsync<T>()`, regardless of which transport is configured.

Unmapped types always fall back to the default `IMessageSerializer` — the raw-first guarantee from ADR-001 is preserved.

### Security and thread-safety note

`MassTransitEnvelopeSerializer` is stateless and thread-safe (uses `[ThreadStatic]` pooled writers with no shared mutable state). It is safe to register as a Singleton and call from any number of threads concurrently. The `ISerializerResolver` built by `AddBareWire` is also immutable after construction — the per-type mapping dictionary is built once at startup and never modified.

## Simulating a MassTransit Producer

For testing, you can publish MassTransit-format messages using the bare `RabbitMQ.Client` without installing MassTransit:

```csharp
var envelope = new
{
    messageId = Guid.NewGuid().ToString(),
    correlationId = Guid.NewGuid().ToString(),
    messageType = new[] { "urn:message:OrderCreated" },
    sentTime = DateTimeOffset.UtcNow,
    message = new { orderId = "abc-123", amount = 99.99m, currency = "PLN" }
};

var props = new BasicProperties
{
    ContentType = "application/vnd.masstransit+json",
    DeliveryMode = DeliveryModes.Persistent
};

await channel.BasicPublishAsync("mt-orders", routingKey: "", props,
    JsonSerializer.SerializeToUtf8Bytes(envelope));
```

> See: `samples/BareWire.Samples.MassTransitInterop/`
