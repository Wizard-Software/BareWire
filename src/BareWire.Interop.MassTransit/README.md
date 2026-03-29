# BareWire.Interop.MassTransit

MassTransit envelope interop deserializer for BareWire. Enables consuming messages published by MassTransit (`application/vnd.masstransit+json`) in a BareWire pipeline without requiring MassTransit as a dependency.

## Installation

```bash
dotnet add package BareWire.Interop.MassTransit
```

## Usage

```csharp
builder.AddBareWire(wire =>
{
    wire.UseMassTransitEnvelopeDeserializer(); // unwrap MassTransit envelope
});
```

## Features

- Deserializes `application/vnd.masstransit+json` envelopes
- Extracts metadata: `MessageId`, `CorrelationId`, `ConversationId`, `SentTime`, headers
- Permissive parsing — unknown envelope fields are silently ignored
- Zero MassTransit runtime dependency

## Dependencies

- `BareWire.Abstractions`
- `BareWire.Serialization.Json`

## Documentation

Full documentation: [BareWire on GitHub](https://github.com/asawicki/BareWire)

## License

MIT
