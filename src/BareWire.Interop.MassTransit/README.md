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

## Dependencies

- `BareWire.Abstractions`
- `BareWire.Serialization.Json`

## Documentation

Full documentation: [BareWire on GitHub](https://github.com/asawicki/BareWire)

## License

MIT
