# BareWire

High-performance async messaging engine with zero-copy pipeline, credit-based flow control, and bounded channels.

BareWire is the core engine that wires up consumers, serializers, and transports into a zero-allocation pipeline. It provides DI integration, hosted service lifecycle, and the publish/consume machinery.

## Installation

```bash
dotnet add package BareWire
```

## Quick Start

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddBareWire(wire =>
{
    wire.AddConsumer<OrderCreatedConsumer>();
    wire.UseJsonSerializer();
    wire.UseRabbitMq(rmq => rmq.Host("localhost"));
});

await builder.Build().RunAsync();
```

## Features

- Zero-copy pipeline using `IBufferWriter<byte>` and `ArrayPool`
- Credit-based flow control with bounded channels
- Publish-side backpressure with configurable limits
- MassTransit-familiar API (`IBus`, `IConsumer<T>`)
- Raw-first: no envelope by default

## Documentation

Full documentation: [BareWire on GitHub](https://github.com/asawicki/BareWire)

## License

MIT
