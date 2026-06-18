# BareWire.Transport.Google.PubSub

Google Cloud Pub/Sub transport adapter for BareWire. Implements `ITransportAdapter` using the low-level `PublisherServiceApiClient` / `SubscriberServiceApiClient` from `Google.Cloud.PubSub.V1` 3.x.

## Capabilities

| Capability | Status | Notes |
|------------|--------|-------|
| `OrderingKeys` | Declared (R5.1) | `BW-OrderingKey` header passed through to `PubsubMessage.OrderingKey`. Full CorrelationId→ordering key mapping in **R5.2**. |
| `BatchReceive` | Active | Pull supports `maxMessages > 1`. |
| `DlqNative` | Declared (R5.1) | `DeadLetterPolicy` args parsed from topology. Full subscription wiring in **R5.3**. |
| `FlowControl` | Active | `MaxOutstandingMessages` / `MaxOutstandingBytes` map 1:1 to `FlowControlOptions`. |

## Configuration

```csharp
services.AddBareWirePubSub(cfg =>
{
    cfg.ProjectId("my-gcp-project");
    cfg.UseApplicationDefaultCredentials();  // default — uses Google ADC
    cfg.AckDeadline(TimeSpan.FromSeconds(60));
    cfg.MaxOutstandingMessages(1000);
    cfg.MaxInFlightMessages(100);
});
```

### Service account JSON key

```csharp
services.AddBareWirePubSub(cfg =>
{
    cfg.ProjectId("my-gcp-project");
    cfg.UseServiceAccountJson("/etc/secrets/sa.json");  // file path
    // or: cfg.UseServiceAccountJsonContent(jsonString); // inline (never log the content)
});
```

### Local emulator

```csharp
services.AddBareWirePubSub(cfg =>
{
    cfg.ProjectId("test-project");
    cfg.UseEmulator("localhost:8085");  // sets AuthMode = EmulatorInsecure
});
```

## Flow control 1:1 mapping

| BareWire `FlowControlOptions` | Pub/Sub equivalent |
|-------------------------------|-------------------|
| `MaxInFlightMessages` (registry limit) | In-flight registry size — messages above this are not pulled |
| `MaxOutstandingMessages` (options) | `maxMessages` per Pull call — limits outstanding message count |
| `InternalQueueCapacity` (flow control) | Bounded `Channel<InboundMessage>` capacity |

## Header mapping

BareWire headers are stored in `PubsubMessage.Attributes` (string→string). Pub/Sub limits:
- Max 100 attributes per message
- Key ≤ 256 UTF-8 bytes; value ≤ 1024 UTF-8 bytes

Violations throw `BareWireTransportException` with only counts/lengths — never key text or values (SEC-4).

## Ordering keys (R5.1)

If the inbound or outbound message has a `BW-OrderingKey` header, it is passed through to/from `PubsubMessage.OrderingKey`. Full CorrelationId→ordering key mapping is implemented in **R5.2**.

> **Security note:** Do not log `ServiceAccountJsonPath` file contents. The path itself is safe to log; the key material is not.

## Roadmap

- **R5.2** — Full ordering key support: `CorrelationId` → `PubsubMessage.OrderingKey`, resume-on-failure (`ResumePublish`).
- **R5.3** — Dead-letter topic wiring: `DeadLetterPolicy` applied to subscriptions from `bw.pubsub.dead-letter-topic` / `bw.pubsub.max-delivery-attempts` topology arguments.
- **R5.4** — Integration tests against the Pub/Sub emulator via Aspire.
