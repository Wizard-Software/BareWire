# BareWire.Kafka

Single-call registration bundle for [BareWire](https://barewire.wizardsoftware.pl) with the
**Kafka** transport.

This package depends on **both** the BareWire core (`BareWire`) and the Kafka transport
(`BareWire.Transport.Kafka`) and exposes one convenience method that registers them together
in a single call.

## Install

```bash
dotnet add package BareWire.Kafka
```

## Usage — single call

```csharp
builder.Services.AddBareWireWithKafka(
    transport =>
    {
        transport.BootstrapServers("localhost:9092");
        transport.ConsumerGroup("order-processing");

        transport.ReceiveEndpoint("orders", e =>
            e.Consumer<OrderCreatedConsumer, OrderCreated>());
    },
    bus =>
    {
        // middleware, serializers...
    });

builder.Services.AddTransient<OrderCreatedConsumer>();
```

The optional `bus` delegate may be omitted when you only need transport defaults:

```csharp
builder.Services.AddBareWireWithKafka(transport => transport.BootstrapServers("localhost:9092"));
```

## Equivalent two-call registration

`AddBareWireWithKafka` is sugar over the explicit two-call form, which remains fully supported
(use it when you need to register multiple transports, or want the core and transport packages
referenced separately):

```csharp
builder.Services.AddBareWireKafka(transport => transport.BootstrapServers("localhost:9092"));
builder.Services.AddBareWire(bus => { /* middleware, serializer mappings... */ });
```

## Layering

The bundle is a thin composition layer over `BareWire` + `BareWire.Transport.Kafka`. The core
never depends on a transport and a transport never depends on the core — the bundle is a
separate layer that references both, preserving the one-directional dependency rule.

See the [BareWire documentation](https://barewire.wizardsoftware.pl) for the full registration
and configuration guide.
