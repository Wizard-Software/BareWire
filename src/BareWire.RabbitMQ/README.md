# BareWire.RabbitMQ

Single-call registration bundle for [BareWire](https://barewire.wizardsoftware.pl) with the
**RabbitMQ** transport.

This package depends on **both** the BareWire core (`BareWire`) and the RabbitMQ transport
(`BareWire.Transport.RabbitMQ`) and exposes one convenience method that registers them
together in a single call.

## Install

```bash
dotnet add package BareWire.RabbitMQ
```

## Usage — single call

```csharp
builder.Services.AddBareWireWithRabbitMq(
    transport => transport.Host("localhost"),
    bus =>
    {
        bus.AddConsumer<OrderConsumer>();
        // endpoints, middleware, serializers...
    });
```

The optional `bus` delegate may be omitted when you only need transport defaults:

```csharp
builder.Services.AddBareWireWithRabbitMq(transport => transport.Host("localhost"));
```

## Equivalent two-call registration

`AddBareWireWithRabbitMq` is sugar over the explicit two-call form, which remains fully
supported (use it when you need to register multiple transports, or want the core and
transport packages referenced separately):

```csharp
builder.Services.AddBareWireRabbitMq(transport => transport.Host("localhost"));
builder.Services.AddBareWire(bus => bus.AddConsumer<OrderConsumer>());
```

## Layering

The bundle is a thin composition layer over `BareWire` + `BareWire.Transport.RabbitMQ`. The
core never depends on a transport and a transport never depends on the core — the bundle is a
separate layer that references both, preserving the one-directional dependency rule.

See the [BareWire documentation](https://barewire.wizardsoftware.pl) for the full registration
and configuration guide.
