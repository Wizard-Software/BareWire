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

## Per-Consumer Envelope (`UseMassTransitEnvelope()`)

The two mechanisms above set the envelope format at the **bus-global** level (`AddMassTransitEnvelopeSerializer` / `MapSerializer<T>`) or the **per-endpoint** level (`UseSerializer<T>` / `UseDeserializer<T>`). A third, narrowest axis selects the format for a **single consumer**: call `UseMassTransitEnvelope()` on the consumer configurator. A consumer marked this way "speaks MassTransit" in both directions — it reads an envelope on the way in and writes one on the way out — independently of whatever default format its endpoint uses.

### The three axes and their precedence

Format resolution runs from the narrowest scope to the widest, and the narrowest active scope wins:

| Scope | How it is set | Precedence |
|-------|---------------|------------|
| Per-consumer | `UseMassTransitEnvelope()` on the consumer | highest |
| Per-endpoint | `UseSerializer<T>()` / `UseDeserializer<T>()` on the receive endpoint | middle |
| Bus-global | default raw JSON, or a globally registered envelope serializer | lowest |

When a consumer is marked, the `Content-Type` header is ignored for that consumer and the MassTransit deserializer is always used. A marked consumer therefore (de)serializes through the MassTransit envelope **regardless** of the endpoint's default deserializer, while an unmarked consumer sharing the same endpoint keeps the endpoint (or global) default.

### Opting a consumer in

`UseMassTransitEnvelope()` is called inside the consumer-configuration delegate of `Consumer<TConsumer, TMessage>`:

```csharp
rmq.ReceiveEndpoint("orders", e =>
{
    // Reads and replies in MassTransit envelope format, whatever the endpoint default is.
    e.Consumer<MtOrderConsumer, OrderCreated>(c => c.UseMassTransitEnvelope());

    // Same endpoint, but this consumer keeps the endpoint/global default format.
    e.Consumer<BwOrderConsumer, OrderShipped>();
});
```

The opt-in is **secure by default** — the envelope format is never enabled for a consumer implicitly. A developer turns it on for one consumer deliberately, and that doubles as an explicit declaration that "this consumer expects a MassTransit envelope", which also disambiguates a delivery that arrives with an absent or ambiguous `Content-Type`. The call returns `void` (matching the configurator convention) and is **idempotent**: it sets an on/off flag, so calling it twice is the same as calling it once. It is orthogonal to the routing-key and `AcceptUntyped()` opt-ins on the same configurator and may be combined with them.

### Receive and reply

The opt-in covers both directions as one coherent interop mode:

- **Receive** — the incoming MassTransit envelope is unwrapped: the envelope's `message` field is deserialized into `TMessage`, and the envelope's `messageType` (a URN) and headers are mapped. The consumer receives a plain record, exactly as it would from a raw BareWire publisher.
- **Reply** — a `RespondAsync` from a marked consumer wraps the response in a MassTransit **response** envelope carrying the correlating `requestId`, so request/response interop with a MassTransit peer round-trips correctly.

This makes a marked consumer a drop-in responder for a MassTransit request client: the request envelope is unwrapped on the way in and the reply envelope is built on the way out, without any envelope-handling code in the consumer itself.

### Mixed consumers on one endpoint

Because the opt-in is per-consumer, a single endpoint can host envelope-speaking and raw consumers side by side (as in the example above). Format selection is resolved independently for each consumer, so adding a MassTransit-speaking consumer to an existing endpoint never changes how the other consumers there deserialize.

### Format-mismatch settlement

If a consumer marked `UseMassTransitEnvelope()` receives a raw, non-envelope payload, the MassTransit deserializer yields no message and the delivery is **negatively settled** (Nack or Reject, depending on the dispatch path and your DLX topology) rather than silently mis-processed — the trust-boundary invariant is that a format mismatch never reaches the consumer as a bad message. One residual edge case is worth knowing: a raw payload that happens to carry a top-level `message` property can parse as a minimal envelope. Enforcing the full envelope shape is the job of a schema-validation middleware; add one when you accept untrusted input (see below).

### Trust boundary with `AcceptUntyped()`

A MassTransit envelope is producer-controlled, unauthenticated input; the safe baseline assumes broker-level publish ACLs gate who can write to the queue. Opting a consumer into the envelope **and** into type-less delivery (`AcceptUntyped()`) at the same time widens the trust boundary to arbitrary foreign JSON. When a consumer combines both without a schema-validation middleware on the endpoint, the bus emits a **startup advisory** so the gap is visible at configuration time. The advisory is specific to that combination — envelope opt-in alone (typed) is a narrower boundary and does not raise it. The mitigation is to add a schema-validation middleware (or rely on enforced publish ACLs) before accepting untyped envelope traffic.

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
    rmq.ConfigureTopology(topo => topo.DeclareExchange("mt-orders", ExchangeType.Fanout, durable: true));
    rmq.DefaultExchange("mt-orders");
    // No ReceiveEndpoint — publish-only bridge
});

services.AddBareWire(bus =>
{
    bus.MapSerializer<OrderCreated, MassTransitEnvelopeSerializer>();
    // All other message types continue using the default raw JSON serializer (raw-first default).
});

// Usage:
await bus.PublishAsync(new OrderCreated(...), ct);
// → Content-Type: application/vnd.masstransit+json

await bus.PublishAsync(new PaymentRequested(...), ct);
// → Content-Type: application/json (default, unaffected)
```

The mapping is bus-global and transport-agnostic: it applies to both `IBus.PublishAsync<T>()` and `ISendEndpoint.SendAsync<T>()`, regardless of which transport is configured.

Unmapped types always fall back to the default `IMessageSerializer` — the raw-first guarantee is preserved.

### Per-type routing to MassTransit (own exchange / routing key)

The MassTransit envelope is the *format*; **where** a bridged message lands is a separate, also
per-type, decision. The two compose — both keyed on the message type — so you can define, per type,
a "MassTransit producer" that writes the envelope **and** targets that type's own exchange and
routing key. MassTransit binds consumers to an exchange named after the message type
(`Namespace:TypeName`), so per-type routing is what makes a real MassTransit peer receive the
message.

```csharp
services.AddBareWireJsonSerializer();
services.AddMassTransitEnvelopeSerializer();

services.AddBareWireRabbitMq(rmq =>
{
    rmq.Host("amqp://guest:guest@localhost:5672/");

    rmq.ConfigureTopology(t =>
    {
        // One exchange per MassTransit-bound type (MT convention: Namespace:TypeName).
        t.DeclareExchange("OrderSystem.Contracts:OrderCreated", ExchangeType.Fanout, durable: true);
        t.DeclareExchange("OrderSystem.Contracts:PaymentRequested", ExchangeType.Topic, durable: true);
    });

    // Routing per type — each MT-bound type to its own exchange (and routing key where it matters).
    rmq.Publish<OrderCreated>(p => p.Exchange("OrderSystem.Contracts:OrderCreated"));
    rmq.Publish<PaymentRequested>(p =>
    {
        p.Exchange("OrderSystem.Contracts:PaymentRequested");
        p.RoutingKey("payment.requested");
    });
});

services.AddBareWire(bus =>
{
    // Format per type — only these two go out as a MassTransit envelope; everything else stays raw JSON.
    bus.MapSerializer<OrderCreated, MassTransitEnvelopeSerializer>();
    bus.MapSerializer<PaymentRequested, MassTransitEnvelopeSerializer>();
});

// Each type now carries the MassTransit envelope AND lands on its own exchange/routing key:
await bus.PublishAsync(new OrderCreated(...), ct);       // → OrderSystem.Contracts:OrderCreated, MT envelope
await bus.PublishAsync(new PaymentRequested(...), ct);   // → OrderSystem.Contracts:PaymentRequested / payment.requested, MT envelope
await bus.PublishAsync(new InternalAuditLogged(...), ct); // → DefaultExchange, raw JSON (unaffected)
```

`MapSerializer<T, …>` (format) lives on the **bus** configurator; `Publish<T>` / `MapExchange<T>` /
`MapRoutingKey<T>` (routing) live on the **RabbitMQ** configurator. They are orthogonal and both
opt-in per type — a type with neither stays raw-first on the default exchange. See
[Publishing and Consuming](publishing-and-consuming.md#ergonomic-per-type-send-mapping) for the full
per-type send-routing reference.

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
