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

The custom serializer must implement `IMessageSerializer`:

```csharp
public sealed class CustomXmlSerializer : IMessageSerializer
{
    public string ContentType => "application/xml";

    public ReadOnlyMemory<byte> Serialize<T>(T message) where T : class
    {
        // Your serialization logic
    }
}
```

## Content-Type Routing

When multiple deserializers are registered globally (e.g. via `AddMassTransitEnvelopeDeserializer()`), the `ContentTypeDeserializerRouter` automatically selects the correct deserializer based on the `Content-Type` header of each incoming message. This happens transparently — no per-endpoint configuration is needed.

Per-endpoint `UseDeserializer<T>()` bypasses the router entirely and uses the specified deserializer for all messages on that endpoint, regardless of content type.

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
