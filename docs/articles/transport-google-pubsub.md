# Google Pub/Sub Transport

BareWire can run on **Google Cloud Pub/Sub** through the `BareWire.Transport.Google.PubSub`
adapter, built on the low-level `PublisherServiceApiClient` / `SubscriberServiceApiClient` from
`Google.Cloud.PubSub.V1`. As with every BareWire transport, you register the **core engine** and
the **transport adapter** together — with the single-call bundle package or the explicit two-call
form.

## Registration

### Single call — bundle package (recommended)

The `BareWire.Google.PubSub` bundle depends on both the core and the Pub/Sub transport and exposes
one method, `AddBareWireWithPubSub`. Configure the transport in the first delegate and (optionally)
the bus in the second:

```csharp
builder.Services.AddBareWireWithPubSub(
    transport => transport.ProjectId("my-gcp-project"),
    bus =>
    {
        bus.AddConsumer<OrderConsumer>();
        // serializers, middleware, endpoints...
    });
```

The `bus` delegate is optional — omit it when transport defaults are enough:

```csharp
builder.Services.AddBareWireWithPubSub(transport => transport.ProjectId("my-gcp-project"));
```

### Two calls — core and transport registered separately

`AddBareWireWithPubSub` is sugar over the explicit pair. Use the two-call form when you reference
the core and transport packages separately, or when an application registers more than one
transport:

```csharp
builder.Services.AddBareWirePubSub(transport => transport.ProjectId("my-gcp-project"));
builder.Services.AddBareWire(bus => bus.AddConsumer<OrderConsumer>());
```

Both paths configure the transport through the same `IPubSubConfigurator` fluent API described
below. See [Configuration](configuration.md) for the general bundle-vs-two-call story.

## Authentication

The transport supports three authentication modes, selected by the configurator. The active mode
is exposed by the `PubSubAuthMode` enum (`ApplicationDefault`, `ServiceAccountJson`,
`EmulatorInsecure`).

### Application Default Credentials (default)

`UseApplicationDefaultCredentials()` uses the Google ADC chain — the `GOOGLE_APPLICATION_CREDENTIALS`
environment variable, the gcloud CLI, Workload Identity, or the Compute Engine metadata server. It
is the default (so the call may be omitted) and the preferred mode for production, because no
secrets are stored in the options.

### Service account JSON key

Supply a service account key either by file path or as inline JSON content:

```csharp
builder.Services.AddBareWirePubSub(cfg =>
{
    cfg.ProjectId("my-gcp-project");
    cfg.UseServiceAccountJson("/etc/secrets/sa.json");   // file path
    // or: cfg.UseServiceAccountJsonContent(jsonString); // inline JSON
});
```

The JSON key content is never logged, never included in diagnostic output, and never echoed in
exception messages. The file path itself is treated as non-secret and may appear in diagnostics —
the key material it points to is not.

### Local emulator

`UseEmulator(endpoint)` connects to a local Pub/Sub emulator over plaintext (insecure) gRPC. It is
intended for local development and integration tests only:

```csharp
builder.Services.AddBareWirePubSub(cfg =>
{
    cfg.ProjectId("test-project");
    cfg.UseEmulator("localhost:8085");  // sets AuthMode = EmulatorInsecure
});
```

An emulator endpoint set under any non-emulator auth mode is rejected at startup, so production
credentials can never be silently downgraded to plaintext gRPC.

## Ordering keys

Call `EnableMessageOrdering()` so subscriptions are created with `enable_message_ordering` during
topology deployment:

```csharp
builder.Services.AddBareWirePubSub(cfg =>
{
    cfg.ProjectId("my-gcp-project");
    cfg.EnableMessageOrdering();
});
```

When a message carries the `BW-OrderingKey` header, that value is passed through to and from the
`PubsubMessage.OrderingKey` field, so messages sharing a key are delivered in order. See
[Per-Key Consumer Ordering](per-key-ordering.md) for the consumer-side "parallel across keys,
ordered within a key" model that pairs with this.

## Dead-letter topics

The adapter wires a native `DeadLetterPolicy` onto subscriptions during topology deployment when
the `bw.pubsub.dead-letter-topic` and `bw.pubsub.max-delivery-attempts` arguments are set: messages
exceeding the configured delivery-attempt count are forwarded to the dead-letter topic instead of
being redelivered indefinitely. For this to work, the subscription's service account requires the
`roles/pubsub.publisher` IAM role on the dead-letter topic — environment/IAM configuration granted
outside BareWire.

## Header mapping

BareWire headers are stored in `PubsubMessage.Attributes` (string-to-string). Pub/Sub enforces a
maximum of 100 attributes per message, keys up to 256 UTF-8 bytes, and values up to 1024 UTF-8
bytes. Violations throw `BareWireTransportException`; the exception reports only counts and lengths,
never the offending key text or values.

## Options

All settings are configured through `IPubSubConfigurator`:

| Method | Default | Meaning |
|--------|---------|---------|
| `ProjectId(string)` | — (required) | Google Cloud project ID. Required in every auth mode. |
| `AckDeadline(TimeSpan)` | 60 s | Acknowledgement deadline applied to subscriptions at topology deploy time. Must be between 10 and 600 seconds. |
| `MaxOutstandingMessages(int)` | 1000 | Maximum messages retrieved per `PullAsync` call — the cap on outstanding unacknowledged messages. |
| `MaxOutstandingBytes(long)` | 67,108,864 (64 MiB) | Maximum total byte size of in-flight message bodies. |
| `MaxInFlightMessages(int)` | 100 | Maximum concurrent in-flight (consumed but not yet settled) messages tracked by the registry. |
| `EnableMessageOrdering()` | off | Creates subscriptions with message ordering enabled. |

```csharp
builder.Services.AddBareWirePubSub(cfg =>
{
    cfg.ProjectId("my-gcp-project");
    cfg.UseApplicationDefaultCredentials();
    cfg.AckDeadline(TimeSpan.FromSeconds(60));
    cfg.MaxOutstandingMessages(1000);
    cfg.MaxOutstandingBytes(64L * 1024 * 1024);
    cfg.MaxInFlightMessages(100);
});
```

`MaxInFlightMessages`, `MaxOutstandingMessages`, and `MaxOutstandingBytes` map directly onto
BareWire's flow-control model: the in-flight registry size, the per-pull message cap, and the
in-flight byte budget.

## See also

- [API reference](../api/index.md)
- [Configuration](configuration.md)
- [Per-Key Consumer Ordering](per-key-ordering.md)
