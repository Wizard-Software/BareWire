# BareWire.CloudEvents

Native dual-mode (binary + structured) CloudEvents 1.0 support for BareWire — zero-copy, no external SDK, depending only on `BareWire.Abstractions` (ADR-007).

## Features

- **Dual content mode (CNCF CloudEvents 1.0)** — both modes the spec mandates:
  - **Binary mode** — CE context attributes are mapped to/from transport headers with the `ce-` prefix; the message payload (`data`) is carried raw, no envelope (ADR-001).
  - **Structured mode** — the full CloudEvents envelope (context attributes + `data`) is serialized as a single `application/cloudevents+json` document; inbound messages are auto-routed by `Content-Type` on the consume path.
- **Zero-copy** — serialization uses `Utf8JsonWriter` over `IBufferWriter<byte>` and deserialization uses `Utf8JsonReader` over `ReadOnlySequence<byte>`, with buffers from `ArrayPool` (ADR-003). The binary path is a raw payload passthrough — no per-message `byte[]` allocation or payload copy on the hot path (binary publish allocates one pre-sized `ce-*` header dictionary by design; see ADR-003 for the allocation budget).
- **Fail-fast validation (FR-5)** — the four mandatory CE attributes (`id`, `source`, `specversion`, `type`) are validated before publish and on read, consistent with BareWire's eager-validation philosophy. An invalid `specversion` (anything other than `"1.0"`) is rejected.
- **Per-endpoint coexistence** — CloudEvents activation never replaces the default serializer (ADR-001, never global replace). Raw JSON remains the default; CloudEvents binary and structured modes coexist with raw endpoints in the same application.
- **No external SDK** — no production dependency on `CloudNative.CloudEvents`; the package depends only on `BareWire.Abstractions`.

## Installation / DI registration

Register the serializers in this order. Order is mandatory:

```csharp
services.AddBareWireJsonSerializer();   // 1. registers the default IMessageSerializer + IDeserializerResolver (raw JSON)
services.AddCloudEvents();              // 2. binary-mode: ce-* header binding (marker singleton)
services.AddCloudEventsEnvelope();      // 3. structured-mode: decorates IDeserializerResolver with a Content-Type router
```

- `AddCloudEvents()` and `AddCloudEventsEnvelope()` each throw `InvalidOperationException` if `AddBareWireJsonSerializer()` was not called first — both build on top of the default raw-JSON serializer (ADR-001) rather than replacing it.
- Both registrations are **idempotent** — calling them more than once is a no-op (the decorator is never stacked).
- `AddCloudEvents()` is required only for binary publish/consume; `AddCloudEventsEnvelope()` is required only for structured consume routing. Register whichever mode(s) the application uses.

## Usage — Binary mode (`ce-*` headers + raw payload)

Binary mode encodes CE context attributes as `ce-*` transport headers and carries the payload raw (ADR-001).

```csharp
services.AddBareWireJsonSerializer();
services.AddCloudEvents();

// Publish: attach CE attributes as ce-* headers (validated fail-fast before publish).
await bus.PublishCloudEventAsync(
    new OrderCreated(orderId),
    new CloudEventContext(
        id: Guid.NewGuid().ToString(),
        source: new Uri("https://shop.example/orders"),
        type: "com.example.order.created"));

// Consume: read CE attributes from the ce-* headers.
public sealed class OrderCreatedConsumer : IConsumer<OrderCreated>
{
    public Task Consume(ConsumeContext<OrderCreated> context)
    {
        // GetCloudEvent() returns null (never throws) when ce-* headers are missing/unparseable
        // and does NOT validate specversion.
        ICloudEventAttributes? ce = context.GetCloudEvent();

        // GetCloudEventOrThrow() is the strict variant: it enforces CE 1.0 compliance
        // (mandatory attributes present, specversion == "1.0") and throws
        // BareWireSerializationException fail-fast otherwise.
        ICloudEventAttributes attrs = context.GetCloudEventOrThrow();

        // attrs.Id, attrs.Source, attrs.Type, attrs.Subject, attrs.Time, attrs.Extensions ...
        return Task.CompletedTask;
    }
}
```

> The `GetCloudEvent()` vs `GetCloudEventOrThrow()` distinction is deliberate: `GetCloudEvent()` follows a "return null, never throw" contract and does not validate `specversion`; `GetCloudEventOrThrow()` is the opt-in throwing variant that enforces `specversion == "1.0"`. Use the throwing variant when strict CE 1.0 compliance is required.

## Usage — Structured mode (`application/cloudevents+json` envelope)

Structured mode encodes the CE attributes and the event data in a single JSON envelope. Inbound `application/cloudevents+json` messages are auto-routed to the CloudEvents deserializer by the Content-Type router installed by `AddCloudEventsEnvelope()`.

```csharp
services.AddBareWireJsonSerializer();
services.AddCloudEvents();           // optional — only needed if the app also uses binary mode
services.AddCloudEventsEnvelope();   // activates structured-mode consume routing

// Publish: build and send a CloudEvents structured envelope (validated fail-fast before publish).
await bus.PublishCloudEventStructuredAsync(
    new OrderCreated(orderId),
    new CloudEventContext(
        id: Guid.NewGuid().ToString(),
        source: new Uri("https://shop.example/orders"),
        type: "com.example.order.created"));

// Consume: the Content-Type router unwraps the envelope; context.Message is the ready-to-use type.
public sealed class OrderCreatedConsumer : IConsumer<OrderCreated>
{
    public Task Consume(ConsumeContext<OrderCreated> context)
    {
        OrderCreated message = context.Message;   // already unwrapped from the CE envelope
        // Note: GetCloudEvent() returns null here — in structured mode the CE attributes live
        // inside the envelope, not in ce-* headers.
        return Task.CompletedTask;
    }
}
```

## Usage — Raw (default, ADR-001)

The default BareWire format is raw JSON with no CE metadata. Publish with the core endpoint methods — no `ce-*` headers, no envelope.

```csharp
await bus.PublishAsync(new OrderCreated(orderId));                    // raw JSON (default serializer)
await bus.PublishRawAsync(payloadMemory, "application/json");         // pre-serialized raw payload
```

## Per-endpoint / per-publish override

The content mode is chosen **per publish call** and **per consume path** — the default serializer is never globally replaced (ADR-001):

| Publish API | Mode | What the consumer sees |
|-------------|------|------------------------|
| `PublishCloudEventAsync(msg, attrs)` | binary | `ce-*` headers + raw payload; read via `GetCloudEvent()` / `GetCloudEventOrThrow()` |
| `PublishCloudEventStructuredAsync(msg, attrs)` | structured | `application/cloudevents+json` envelope; auto-routed by Content-Type, `context.Message` is the unwrapped type |
| `PublishAsync(msg)` / `PublishRawAsync(...)` | raw (default) | plain JSON, no `ce-*` headers, no envelope (ADR-001) |

All three modes coexist in one application: raw JSON, binary CloudEvents, and structured CloudEvents endpoints run side by side. CloudEvents is purely additive on top of the raw-first default.

## Limitations

- **No certified AMQP 1.0 protocol binding (R1).** On RabbitMQ, binary mode maps `ce-*` attributes to `BasicProperties.Headers` over **AMQP 0-9-1**. This is documented as **"CloudEvents-over-RabbitMQ (AMQP 0-9-1)"**, NOT as a certified CloudEvents AMQP 1.0 protocol binding (the formal binding targets AMQP 1.0, which is a different protocol than RabbitMQ's AMQP 0-9-1).
- **Deferred beyond MVP:**
  - `data_base64` (binary payload in structured mode) — in MVP `data` is JSON inline.
  - Batch mode (`application/cloudevents-batch+json`).
  - Protocol bindings for Kafka, MQTT, and HTTP.

## Security note (SEC-2)

CE context attributes in binary mode are carried as **transport headers** (`ce-*`), which are visible to the broker and to any header-logging middleware — they are NOT part of the payload. Treat `ce-*` attributes as routing/correlation metadata only:

- **Sensitive data belongs in the payload (`data`), never in CE context attributes (`ce-*`).**

## References

- [ADR-007 — CloudEvents envelope/binding strategy](../../.forge/docs/architecture/decisions/ADR-007-cloudevents-envelope-binding.md)
- [ADR-001 — Raw-first, no envelope by default](../../.forge/docs/architecture/decisions/ADR-001-raw-first-no-envelope.md)
- [ADR-003 — Zero-copy pipeline](../../.forge/docs/architecture/decisions/ADR-003-zero-copy-pipeline.md)
- Sample: `samples/BareWire.Samples.CloudEventsInterop` — runnable binary + structured + raw interop demo.
