# MessagePack Serialization

`BareWire.Serialization.MsgPack` adds a [MessagePack](https://msgpack.org/) serializer and deserializer that plug into BareWire's zero-copy pipeline. MessagePack is a compact binary format: payloads are smaller than JSON and encode/decode faster, which lowers bandwidth and per-message allocation on high-throughput streams. The package stays raw-first — it produces a bare MessagePack body with no envelope, exactly like the default raw-JSON serializer.

It is backed by MessagePack-CSharp with the `ContractlessStandardResolver`, so plain `record` message types work without `[MessagePackObject]` attributes. The serializer reports a content type of `application/x-msgpack`.

```bash
dotnet add package BareWire.Serialization.MsgPack
```

## Registration

The package exposes two extension methods on `IServiceCollection`. They do different jobs, and most setups call them together.

### `AddBareWireMessagePackSerializer()`

Registers the serializer and deserializer in DI:

- `MessagePackSerializer` as `IMessageSerializer`
- `MessagePackDeserializer` as `IMessageDeserializer`
- `MessagePackDeserializer` under its concrete type as well, so per-endpoint overrides (`UseDeserializer<MessagePackDeserializer>()`) resolve at bus start

All three are singletons (the types are stateless). Registration uses `TryAdd*`, so a custom serializer registered earlier is not replaced — call this method *after* any custom serializer registration. This method does **not** activate Content-Type routing; the default raw-JSON consume path is left untouched.

```csharp
// Publish path: MessagePack becomes the IMessageSerializer
services.AddBareWireMessagePackSerializer();
```

### `AddBareWireMessagePackDeserializerRouting()`

Activates Content-Type routing on the consume path by **decorating** the existing `IDeserializerResolver`. After this call, inbound messages with `Content-Type: application/x-msgpack` are routed to the MessagePack deserializer; every other content type (including `application/json` and `null`) continues to use the underlying raw-JSON deserializer.

This method requires a base `IDeserializerResolver` to already be registered — register the JSON serializer first. If none is present it throws `InvalidOperationException` (fail-fast). Calling the method more than once is idempotent: the decorator is never stacked.

```csharp
// 1. Base resolver (raw-JSON) — REQUIRED first
services.AddBareWireJsonSerializer();

// 2. Optional: register the MsgPack serializer for the publish path
services.AddBareWireMessagePackSerializer();

// 3. Activate Content-Type routing — decorates the resolver from step 1
services.AddBareWireMessagePackDeserializerRouting();
```

> **Order matters.** `AddBareWireMessagePackDeserializerRouting()` must run *after* `AddBareWireJsonSerializer()` (or anything else that registers an `IDeserializerResolver`). Calling it first throws `InvalidOperationException`.

## Bus-Level vs Per-Endpoint Use

Use `AddBareWireMessagePackSerializer()` when MessagePack is your default wire format for outgoing messages — it sets `MessagePackSerializer` as the application-wide `IMessageSerializer`.

To force MessagePack on a single receive endpoint regardless of the incoming `Content-Type`, override the deserializer on that endpoint:

```csharp
wire.ReceiveEndpoint("my-queue", ep =>
{
    ep.UseDeserializer<MessagePackDeserializer>();
    ep.Consumer<MyConsumer, MyMessage>();
});
```

A per-endpoint override bypasses Content-Type routing entirely and applies to every message on that endpoint. It requires `AddBareWireMessagePackSerializer()` so the concrete `MessagePackDeserializer` is resolvable from DI. See [Custom Serializers](custom-serializers.md) for the general per-endpoint override mechanism.

## Content-Type Deserializer Routing

When endpoints carry mixed payloads — some raw JSON, some MessagePack — use routing instead of a per-endpoint override. Once `AddBareWireMessagePackDeserializerRouting()` is registered, the router selects the deserializer per message from its `Content-Type` header:

| Inbound `Content-Type`              | Deserializer used        |
|-------------------------------------|--------------------------|
| `application/x-msgpack`             | `MessagePackDeserializer` |
| `application/json`, any other value | inner resolver (raw-JSON) |
| `null` / unregistered              | inner resolver (raw-JSON) |

Matching is **exact**, case-insensitive. Parameterised variants such as `application/x-msgpack; charset=utf-8` do **not** match and fall through to raw-JSON (fail-closed). This keeps the raw-first default intact: anything the router does not explicitly recognise stays on the JSON path.

## When to Use What

| Scenario | Approach |
|---|---|
| Publish everything as MessagePack | `AddBareWireMessagePackSerializer()` (bus-level) |
| One endpoint is always MessagePack | `ep.UseDeserializer<MessagePackDeserializer>()` |
| Mixed JSON + MessagePack on the same endpoints | `AddBareWireJsonSerializer()` + `AddBareWireMessagePackDeserializerRouting()` |

## Security and Type Requirements

The serializer and deserializer share a hardened options profile: the `UntrustedData` security mode (SipHash-seeded hashing and a recursion-depth limit to resist denial-of-service), with LZ4 compression and Typeless resolvers deliberately disabled. Deserialization always targets a known, closed CLR type via `Deserialize<T>`. On failure both paths throw `BareWireSerializationException`; the deserializer never embeds the raw binary payload in the exception.

> **Message types must be `public`.** The `ContractlessStandardResolver` generates formatters only for `public` types at runtime.

Both operations are zero-copy: the serializer writes straight to an `IBufferWriter<byte>`, and the deserializer reads straight from a `ReadOnlySequence<byte>` (including multi-segment sequences) without copying into a contiguous buffer. An empty sequence deserializes to `null`.

## See also

- [API Reference](../api/index.md)
- [Custom Serializers](custom-serializers.md)
- [Configuration](configuration.md)
