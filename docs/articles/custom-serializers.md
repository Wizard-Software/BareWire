# Custom Serializers

BareWire supports overriding the serializer or deserializer on a per-endpoint basis. This is useful when different queues carry messages in different formats — for example, one queue receives raw JSON from an external system while another uses BareWire's envelope format.

## Global vs Per-Endpoint

By default, all endpoints share the global serializer registered via `AddBareWireJsonSerializer()`. You can override this at two levels:

- **Bus-level** — `cfg.UseSerializer<T>()` replaces the default serializer for all endpoints
- **Endpoint-level** — `e.UseSerializer<T>()` or `e.UseDeserializer<T>()` overrides only that endpoint

Endpoint-level overrides take precedence over the bus-level default.

## Per-Endpoint Deserializer

Use `UseDeserializer<T>()` on a receive endpoint to override how incoming messages are decoded:

```csharp
rmq.ReceiveEndpoint("legacy-queue", e =>
{
    e.Consumer<LegacyConsumer, LegacyEvent>();
    e.UseDeserializer<CustomXmlDeserializer>();
});

rmq.ReceiveEndpoint("standard-queue", e =>
{
    e.Consumer<StandardConsumer, OrderCreated>();
    // Uses global SystemTextJsonRawDeserializer — no override needed
});
```

The custom deserializer must implement `IMessageDeserializer` and be registered in DI:

```csharp
public sealed class CustomXmlDeserializer : IMessageDeserializer
{
    public string ContentType => "application/xml";

    public T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class
    {
        // Your deserialization logic
    }
}

// Register in DI
builder.Services.AddSingleton<CustomXmlDeserializer>();
```

## Per-Endpoint Serializer

Use `UseSerializer<T>()` on a receive endpoint to override how outgoing messages (published from within a consumer) are encoded:

```csharp
rmq.ReceiveEndpoint("xml-queue", e =>
{
    e.Consumer<XmlConsumer, XmlMessage>();
    e.UseSerializer<CustomXmlSerializer>();
});
```

The custom serializer must implement `IMessageSerializer`. Serialization is zero-copy — you write
straight into the supplied `IBufferWriter<byte>` rather than returning a `byte[]`:

```csharp
public sealed class CustomXmlSerializer : IMessageSerializer
{
    public string ContentType => "application/xml";

    public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class
    {
        // Write the serialized bytes directly into `output` — no intermediate byte[] allocation.
    }
}
```

## Content-Type Routing

When multiple deserializers are registered globally (e.g. via `AddMassTransitEnvelopeDeserializer()`), the `ContentTypeDeserializerRouter` automatically selects the correct deserializer based on the `Content-Type` header of each incoming message. This happens transparently — no per-endpoint configuration is needed.

Per-endpoint `UseDeserializer<T>()` bypasses the router entirely and uses the specified deserializer for all messages on that endpoint, regardless of content type.

## Adding Outbound Headers (`IOutboundHeaderContributor`)

A serializer that needs to put extra **transport headers** on the outgoing message (not just bytes in the body) can also implement `IOutboundHeaderContributor`:

```csharp
public sealed class MySerializer : IMessageSerializer, IOutboundHeaderContributor
{
    // ... Serialize / ContentType ...

    public void ContributeHeaders(IDictionary<string, string> headers)
    {
        // Called after user headers, framework headers and the trace context are written.
        if (headers.TryGetValue("traceparent", out var traceparent))
        {
            headers["X-My-Trace"] = traceparent;
        }
    }
}
```

The bus calls `ContributeHeaders` as the last header step for `PublishAsync`, `ISendEndpoint.SendAsync` and the RabbitMQ request client, on the serializer resolved for the message type. Rules:

- **Only add** your own headers. Do not modify or remove existing ones (`BW-*`, `message-id`, `traceparent`); this is a documented contract, not enforced at runtime.
- The implementation must be thread-safe and must not throw.
- The default raw serializers do not implement it (raw-first). The built-in `MassTransitEnvelopeSerializer` uses it to write `MT-Activity-Id` — see [MassTransit Interop](masstransit-interop.md#distributed-tracing).

## When to Use What

| Scenario | Approach |
|---|---|
| All endpoints use the same format | Global `AddBareWireJsonSerializer()` — no overrides |
| One endpoint receives a different format | `e.UseDeserializer<T>()` on that endpoint |
| One endpoint publishes in a different format | `e.UseSerializer<T>()` on that endpoint |
| Multiple formats on the same endpoint | Global `ContentTypeDeserializerRouter` (register additional deserializers) |
| Publishing to MassTransit from one endpoint | `AddMassTransitEnvelopeSerializer()` + `e.UseSerializer<MassTransitEnvelopeSerializer>()` |
| External system with custom wire format | `e.UseDeserializer<T>()` with a custom `IMessageDeserializer` |

> See: [MassTransit Interop](masstransit-interop.md) for bidirectional interop with MassTransit (consume + publish)
