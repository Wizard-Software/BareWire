# BareWire.Interop.MassTransit

MassTransit envelope serializer and deserializer for BareWire. Enables full bidirectional interoperability with MassTransit — consuming messages published by MassTransit (`application/vnd.masstransit+json`) and publishing messages in MassTransit envelope format — without requiring MassTransit as a dependency.

## Installation

```bash
dotnet add package BareWire.Interop.MassTransit
```

## Features

- **Deserializer** — transparently unwraps `application/vnd.masstransit+json` envelopes; consumers receive a plain message object regardless of the source format
- **Serializer** — publishes messages wrapped in a MassTransit-compatible envelope; activatable per receive endpoint via `UseSerializer<MassTransitEnvelopeSerializer>()` without replacing the default raw JSON serializer
- **Publish-only bridge (no receive endpoint)** — use `IBusConfigurator.MapSerializer<TMessage, MassTransitEnvelopeSerializer>()` to forward specific message types in MassTransit envelope format without declaring any receive endpoint
- Extracts envelope metadata: `MessageId`, `CorrelationId`, `ConversationId`, `SentTime`, headers
- Permissive parsing — unknown envelope fields are silently ignored
- Zero MassTransit runtime dependency
- ADR-001 compliant — raw JSON remains the default; envelope format requires explicit opt-in

## Usage

```csharp
// Register the base JSON serializer first (required by both interop extensions).
services.AddBareWireJsonSerializer();

// Register the envelope deserializer — enables consuming messages from MassTransit.
// Wires MassTransitEnvelopeDeserializer into ContentTypeDeserializerRouter for
// application/vnd.masstransit+json. Must be called after AddBareWireJsonSerializer().
services.AddMassTransitEnvelopeDeserializer();

// Register the envelope serializer in DI for per-endpoint use.
// Does NOT replace the default raw JSON serializer (ADR-001 raw-first).
// Must be called after AddBareWireJsonSerializer().
services.AddMassTransitEnvelopeSerializer();
```

## Per-endpoint override

Use `UseSerializer<MassTransitEnvelopeSerializer>()` on a receive endpoint to publish messages in MassTransit envelope format for that endpoint only. All other endpoints continue using the default raw JSON serializer.

```csharp
rmq.ReceiveEndpoint("mt-envelope-queue", e =>
{
    e.UseSerializer<MassTransitEnvelopeSerializer>();
    e.Consumer<MyConsumer, MyMessage>();
});
```

## Publish-only bridge (no receive endpoint)

When your application only publishes to MassTransit (no receive endpoints), use `MapSerializer<TMessage, MassTransitEnvelopeSerializer>()` on the bus configurator:

```csharp
services.AddBareWireJsonSerializer();
services.AddMassTransitEnvelopeSerializer();

services.AddBareWire(bus =>
{
    bus.MapSerializer<OrderCreated, MassTransitEnvelopeSerializer>();
    // Other types continue using the default raw JSON serializer.
});
```

See [doc/masstransit-interop.md](../../../doc/masstransit-interop.md) for full documentation and the publish-only bridge scenario.

## BareWire → MassTransit request/response

When BareWire acts as the **request caller** and MassTransit acts as the **responder**, the serializer must emit the complete MassTransit request envelope so that MassTransit's `context.RespondAsync(...)` can route the reply back to BareWire's exclusive reply queue. Without these fields, MT falls back to publish and the reply never arrives (GH #19).

### Opt-in per message type

Use `MapSerializer<TRequest, MassTransitEnvelopeSerializer>()` on the bus configurator for the request message type:

```csharp
services.AddBareWireJsonSerializer();
services.AddMassTransitEnvelopeSerializer();
services.AddMassTransitEnvelopeDeserializer(); // needed to read MT response envelopes

services.AddBareWire(bus =>
{
    bus.MapSerializer<PingRequest, MassTransitEnvelopeSerializer>();
    // Response type uses the envelope deserializer registered above.
});
```

### What the serializer emits

When a request is sent through `IRequestClient<TRequest>.GetResponseAsync<TResponse>()`, the envelope serializer writes the full MassTransit request envelope:

| Field | Value |
|-------|-------|
| `messageId` | Per-message GUID |
| `requestId` | GUID identifying this request (MT correlates responses by this field) |
| `responseAddress` | `rabbitmq://host/vhost/amq.gen-xyz?temporary=true` — BareWire's exclusive reply queue |
| `destinationAddress` | `rabbitmq://host/vhost/target-queue` — the target queue |
| `faultAddress` | Same as `responseAddress` — fault messages also return to the reply queue |
| `expirationTime` | ISO 8601 UTC timestamp derived from the request client timeout |
| `messageType` | MassTransit URN array |
| `sentTime` | ISO 8601 UTC send time |
| `message` | The serialized request payload |

### Response correlation

MassTransit responds by echo-ing `requestId` back in the response envelope (it does not set AMQP `correlation_id` to a matchable value). BareWire's `RabbitMqRequestClient` uses a two-step correlation strategy:

1. **Primary:** AMQP `CorrelationId` header — works for BareWire↔BareWire and transports that echo `correlation_id`.
2. **Fallback:** when AMQP `CorrelationId` is absent or unknown and `content-type == application/vnd.masstransit+json`, the `requestId` field is extracted from the response envelope via `IResponseEnvelopeReader` and matched against the pending-request table.

### AMQP TTL (GH #18)

An AMQP `expiration` header (TTL in milliseconds) is set on every outgoing request. This ensures that unconsumed requests expire automatically on the broker, preventing stale work when the caller times out before the responder processes the message.

## Dependencies

- `BareWire.Abstractions`
- `BareWire.Serialization.Json`

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
