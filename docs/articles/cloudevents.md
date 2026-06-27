# CloudEvents

BareWire can publish and consume messages as [CloudEvents 1.0](https://cloudevents.io/) without taking a dependency on the `CloudNative.CloudEvents` SDK — the `BareWire.CloudEvents` package depends only on `BareWire.Abstractions`. It supports both content modes the spec mandates: **binary mode** (CE attributes carried as `ce-*` transport headers, payload raw) and **structured mode** (the whole event serialized as a single `application/cloudevents+json` envelope). CloudEvents is purely additive: the raw-JSON default is never replaced, so raw, binary-CE, and structured-CE endpoints coexist in one application.

## Registration

Register the serializers in this order — order is mandatory. Both CloudEvents methods build on top of the default raw-JSON serializer and throw `InvalidOperationException` if `AddBareWireJsonSerializer()` was not called first.

```csharp
builder.Services.AddBareWireJsonSerializer(); // 1. default raw-JSON serializer + deserializer resolver
builder.Services.AddCloudEvents();            // 2. binary mode: ce-* header binding
builder.Services.AddCloudEventsEnvelope();    // 3. structured mode: Content-Type router on the consume path
```

The two registrations do different things:

- **`AddCloudEvents()`** activates **binary mode**. It registers a marker singleton (`CloudEventsBinaryActivation`) that signals binary mode is active and unlocks the `ce-*` header binding used by `PublishCloudEventAsync` and the consume-side `GetCloudEvent` extensions. It uses `TryAddSingleton` and never replaces the default `IMessageSerializer`.
- **`AddCloudEventsEnvelope()`** activates **structured-mode consume routing**. It decorates the existing `IDeserializerResolver` with a `Content-Type` router: inbound messages with `Content-Type: application/cloudevents+json` are routed to the CloudEvents envelope deserializer, while every other content type (including `application/json` and `null`) continues to use the default raw-JSON path.

Both methods are **idempotent** — calling either one more than once is a no-op (the decorator is never stacked). Register whichever mode(s) the application actually uses: `AddCloudEvents()` only for binary publish/consume, `AddCloudEventsEnvelope()` only for structured consume routing.

## Binary mode vs structured mode

The two modes differ in *where* the CloudEvents context attributes travel, and that difference is visible only on the consume side:

| | Binary mode | Structured mode |
|---|---|---|
| Publish API | `PublishCloudEventAsync(msg, attrs)` | `PublishCloudEventStructuredAsync(msg, attrs)` |
| Wire shape | `ce-*` transport headers + raw payload | single `application/cloudevents+json` document (attributes + `data`) |
| Allocation | no per-message `byte[]`; payload is a raw passthrough (one pre-sized `ce-*` header dictionary is allocated by design) | the envelope is written to a buffer that grows to fit the document |
| Consume routing | none — the consumer opts in by calling `GetCloudEvent()` | automatic, by `Content-Type` (the router unwraps the envelope before the consumer runs) |
| Reading attributes | `context.GetCloudEvent()` / `GetCloudEventOrThrow()` | `context.Message` is the unwrapped type; `GetCloudEvent()` returns `null` (attributes live inside the envelope, not in `ce-*` headers) |

## Mandatory-attribute validation

CloudEvents 1.0 defines four mandatory context attributes — `id`, `source`, `specversion`, `type`. Both publish methods validate these **fail-fast before publish**: if any mandatory attribute is missing or empty, or if `specversion` is anything other than `"1.0"`, a `BareWireSerializationException` is thrown and the transport is never touched.

`CloudEventContext` itself null-guards its four mandatory constructor parameters (`id`, `source`, `type`, `specVersion`) but does not enforce the CE 1.0 domain rules — that is the job of the fail-fast validator on the publish path and of `GetCloudEventOrThrow()` on the read path.

## Publishing with CloudEvent attributes

Build a `CloudEventContext` and pass it to the publish extension. `CloudEventContext` is immutable (all properties are `init`-only); the constructor takes the four mandatory attributes plus optional ones (`subject`, `time`, `dataContentType`, `dataSchema`, `extensions`). `specVersion` defaults to `"1.0"`.

```csharp
var attrs = new CloudEventContext(
    id: Guid.NewGuid().ToString(),
    source: new Uri("/samples/cloudevents-interop/binary", UriKind.Relative),
    type: "com.barewire.sample.shipment.dispatched",
    specVersion: "1.0",
    time: DateTimeOffset.UtcNow);

// Binary mode: ce-* headers + raw payload.
await bus.PublishCloudEventAsync(new ShipmentDispatched(id, destination, carrier), attrs, ct);

// Structured mode: a single application/cloudevents+json envelope.
await bus.PublishCloudEventStructuredAsync(new ShipmentDispatched(id, destination, carrier), attrs, ct);
```

Both extensions hang off `IPublishEndpoint`, so the same call works from `IBus` and from inside a consumer's `ConsumeContext`. A raw publish — `bus.PublishAsync(msg)` — emits plain JSON with no `ce-*` headers and no envelope, the raw-first default.

## Reading attributes on the consume side

In **binary mode**, the consumer reads the CE attributes from the `ce-*` headers via two extension methods on `ConsumeContext`:

- **`GetCloudEvent()`** follows a "return `null`, never throw" contract. It returns `null` when any of the four mandatory `ce-*` headers (`ce-id`, `ce-source`, `ce-specversion`, `ce-type`) is missing or unparseable, and it does **not** validate `specversion`. Use it as a safe guard.
- **`GetCloudEventOrThrow()`** is the strict variant: it enforces CE 1.0 compliance (all mandatory attributes present, `specversion == "1.0"`) and throws `BareWireSerializationException` otherwise.

```csharp
public sealed class BinaryAwareConsumer : IConsumer<ShipmentDispatched>
{
    public Task ConsumeAsync(ConsumeContext<ShipmentDispatched> context)
    {
        // Safe guard: null when the message has no ce-* headers (e.g. it arrived raw or structured).
        ICloudEventAttributes? ce = context.GetCloudEvent();
        if (ce is not null)
        {
            // Strict CE 1.0 read: validates specversion == "1.0".
            ICloudEventAttributes attrs = context.GetCloudEventOrThrow();
            // attrs.Id, attrs.Source, attrs.Type, attrs.Subject, attrs.Time,
            // attrs.DataContentType, attrs.DataSchema, attrs.Extensions ...
        }

        return Task.CompletedTask;
    }
}
```

Both methods return `ICloudEventAttributes`, the read-only view of the standard CE attributes. The `CloudEventsInterop` sample fans one logical `ShipmentDispatched` event out to three queues to make the difference concrete: the binary reader sees populated `ce-*` attributes, while the structured and raw readers both get `null` from `GetCloudEvent()`.

In **structured mode**, no consume-side call is needed for the payload — the `Content-Type` router unwraps the envelope and deserializes the `data` field before the consumer runs, so `context.Message` is the ready-to-use type. `GetCloudEvent()` returns `null` here, because the CE attributes live inside the JSON envelope rather than in `ce-*` transport headers.

## Security notes

In binary mode the `ce-*` attributes are **transport headers** — visible to the broker and to any header-logging middleware, and not part of the payload. Treat them as routing/correlation metadata only: **sensitive data belongs in the payload (`data`), never in CE context attributes.** Inbound `ce-*` headers are sender-controlled, so parsing is defensive (`GetCloudEvent()` never throws on malformed input), and exception messages from `GetCloudEventOrThrow()` that echo sender values are sanitized to prevent log injection. Structured-mode deserialization additionally applies bounded hardening limits (default 256 KiB envelope size and a capped nesting depth) so a hostile envelope is rejected before any costly parsing.

## See also

- [API Reference](../api/index.md)
- [Custom Serializers](custom-serializers.md)
- [Publishing and Consuming](publishing-and-consuming.md)
