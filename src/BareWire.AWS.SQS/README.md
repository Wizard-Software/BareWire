# BareWire.AWS.SQS

Single-call registration bundle for [BareWire](https://barewire.wizardsoftware.pl) with the
**AWS SQS** transport.

This package depends on **both** the BareWire core (`BareWire`) and the AWS SQS transport
(`BareWire.Transport.AWS.SQS`) and exposes one convenience method that registers them together
in a single call.

## Install

```bash
dotnet add package BareWire.AWS.SQS
```

## Usage — single call

```csharp
builder.Services.AddBareWireWithSqs(
    transport => transport.Region("eu-central-1"),
    bus =>
    {
        bus.AddConsumer<OrderConsumer>();
        // endpoints, middleware, serializers...
    });
```

The optional `bus` delegate may be omitted when you only need transport defaults:

```csharp
builder.Services.AddBareWireWithSqs(transport => transport.Region("eu-central-1"));
```

## Equivalent two-call registration

`AddBareWireWithSqs` is sugar over the explicit two-call form, which remains fully supported
(use it when you need to register multiple transports, or want the core and transport packages
referenced separately):

```csharp
builder.Services.AddBareWireSqs(transport => transport.Region("eu-central-1"));
builder.Services.AddBareWire(bus => bus.AddConsumer<OrderConsumer>());
```

## Layering

The bundle is a thin composition layer over `BareWire` + `BareWire.Transport.AWS.SQS`. The core
never depends on a transport and a transport never depends on the core — the bundle is a
separate layer that references both, preserving the one-directional dependency rule.

See the [BareWire documentation](https://barewire.wizardsoftware.pl) for the full registration
and configuration guide.
