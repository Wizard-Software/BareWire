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

builder.Services.AddBareWireJsonSerializer();          // BareWire.Serialization.Json
builder.Services.AddTransient<OrderCreatedConsumer>(); // consumers are resolved from DI

builder.Services.AddBareWireRabbitMq(transport =>      // BareWire.Transport.RabbitMQ
{
    transport.Host("amqp://guest:guest@localhost:5672/");
    transport.ConfigureTopology(topology =>
    {
        topology.DeclareExchange("orders", ExchangeType.Fanout, durable: true);
        topology.DeclareQueue("orders", durable: true);
        topology.BindExchangeToQueue("orders", "orders", routingKey: "");
    });
    transport.DefaultExchange("orders");
    transport.ReceiveEndpoint("orders", e => e.Consumer<OrderCreatedConsumer, OrderCreated>());
});

builder.Services.AddBareWire(bus => { /* middleware, serializer mappings... */ });

await builder.Build().RunAsync();
```

## Features

- Zero-copy pipeline using `IBufferWriter<byte>` and `ArrayPool`
- Credit-based flow control with bounded channels
- Publish-side backpressure with configurable limits
- MassTransit-familiar API (`IBus`, `IConsumer<T>`)
- Raw-first: no envelope by default

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
