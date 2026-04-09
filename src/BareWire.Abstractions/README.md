# BareWire.Abstractions

Core interfaces and contracts for BareWire messaging library. Zero dependencies.

This package contains all public interfaces (`IBus`, `IConsumer<T>`, `ConsumeContext<T>`, etc.) that define the BareWire messaging contract. It has no external dependencies, making it safe to reference from any layer of your application.

## Installation

```bash
dotnet add package BareWire.Abstractions
```

## Key Interfaces

- `IBus` — publish and send messages
- `IConsumer<T>` — consume messages of type `T`
- `ConsumeContext<T>` — message context with headers, retry info, and cancellation
- `ISerializer` — serialization contract for transports
- `ITransport` — transport abstraction for pluggable providers

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
