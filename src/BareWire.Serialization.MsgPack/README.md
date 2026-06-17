# BareWire.Serialization.MsgPack

Zero-copy MessagePack serializer and deserializer for BareWire. Backed by MessagePack-CSharp with the `ContractlessStandardResolver` and an `UntrustedData` security profile.

## Installation

```bash
dotnet add package BareWire.Serialization.MsgPack
```

## Usage

### Serializer only (publish path)

```csharp
// Registers IMessageSerializer + IMessageDeserializer + concrete MessagePackDeserializer
services.AddBareWireMessagePackSerializer();
```

### With Content-Type routing (consume path)

To route inbound messages whose `Content-Type` is `application/x-msgpack` to the MessagePack
deserializer, register the routing decorator **after** the base JSON deserializer resolver:

```csharp
// 1. Register the base IDeserializerResolver (raw-JSON, ADR-001)
services.AddBareWireJsonSerializer();

// 2. Optionally register the MsgPack serializer for the publish path
services.AddBareWireMessagePackSerializer();

// 3. Activate Content-Type routing — decorates the existing IDeserializerResolver
services.AddBareWireMessagePackDeserializerRouting();
```

> **Registration order matters.** `AddBareWireMessagePackDeserializerRouting()` must be called
> *after* `AddBareWireJsonSerializer()` (or any other method that registers an
> `IDeserializerResolver`). Calling it first throws `InvalidOperationException`.

### Per-endpoint override

To force all messages on a specific endpoint to use MessagePack regardless of `Content-Type`:

```csharp
wire.ReceiveEndpoint("my-queue", ep =>
{
    ep.UseDeserializer<MessagePackDeserializer>();
    ep.Consumer<MyConsumer, MyMessage>();
});
```

This requires `AddBareWireMessagePackSerializer()` to be registered (the concrete type must be
resolvable from DI).

## Features

- Zero-copy serialization via `IBufferWriter<byte>` (ADR-003)
- Zero-copy deserialization via `ReadOnlySequence<byte>` including multi-segment sequences
- Content-Type routing: `application/x-msgpack` → `MessagePackDeserializer` (fail-closed exact-match — parameterised variants such as `application/x-msgpack; charset=utf-8` fall through to raw-JSON)
- `UntrustedData` security profile: SipHash seed, recursion-depth limit, no Typeless/LZ4 (ADR-013)
- Raw-first default preserved: unregistered or `null` Content-Type falls back to raw-JSON (ADR-001)
- Idempotent registration: calling `AddBareWireMessagePackDeserializerRouting()` multiple times is safe

> **Note:** Message types used with MessagePack must be `public`. The `ContractlessStandardResolver`
> generates formatters only for `public` types at runtime.

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
