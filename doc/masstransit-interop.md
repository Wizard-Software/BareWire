# MassTransit Interop

BareWire can consume messages published by MassTransit without requiring MassTransit as a runtime dependency. The `BareWire.Interop.MassTransit` package adds a content-type-aware deserializer that unwraps MassTransit's envelope format transparently.

## Installation

```bash
dotnet add package BareWire.Interop.MassTransit
```

## Configuration

Register the MassTransit envelope deserializer **after** the base JSON serializer:

```csharp
builder.Services.AddBareWireJsonSerializer();
builder.Services.AddMassTransitEnvelopeDeserializer();
```

The order matters — `AddMassTransitEnvelopeDeserializer()` throws `InvalidOperationException` if called before `AddBareWireJsonSerializer()`.

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
