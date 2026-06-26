# BareWire.Google.PubSub

Single-call registration bundle for [BareWire](https://barewire.wizardsoftware.pl) with the
**Google Cloud Pub/Sub** transport.

This package depends on **both** the BareWire core (`BareWire`) and the Google Cloud Pub/Sub
transport (`BareWire.Transport.Google.PubSub`) and exposes one convenience method that registers
them together in a single call.

## Install

```bash
dotnet add package BareWire.Google.PubSub
```

## Usage — single call

```csharp
builder.Services.AddBareWireWithPubSub(
    transport => transport.ProjectId("my-gcp-project"),
    bus =>
    {
        bus.AddConsumer<OrderConsumer>();
        // endpoints, middleware, serializers...
    });
```

The optional `bus` delegate may be omitted when you only need transport defaults:

```csharp
builder.Services.AddBareWireWithPubSub(transport => transport.ProjectId("my-gcp-project"));
```

## Equivalent two-call registration

`AddBareWireWithPubSub` is sugar over the explicit two-call form, which remains fully supported
(use it when you need to register multiple transports, or want the core and transport packages
referenced separately):

```csharp
builder.Services.AddBareWirePubSub(transport => transport.ProjectId("my-gcp-project"));
builder.Services.AddBareWire(bus => bus.AddConsumer<OrderConsumer>());
```

## Layering

The bundle is a thin composition layer over `BareWire` + `BareWire.Transport.Google.PubSub`. The
core never depends on a transport and a transport never depends on the core — the bundle is a
separate layer that references both, preserving the one-directional dependency rule.

See the [BareWire documentation](https://barewire.wizardsoftware.pl) for the full registration
and configuration guide.
