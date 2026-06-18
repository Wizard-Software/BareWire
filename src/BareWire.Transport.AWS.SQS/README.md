# BareWire.Transport.AWS.SQS

Amazon SQS transport adapter for BareWire. Implements `ITransportAdapter` with long-polling consumer, batched send (up to 10 messages per SQS batch), credit-based flow control (ADR-004), and native DLQ via RedrivePolicy.

## Configuration

```csharp
// DefaultChain (recommended for production — IAM roles, environment variables)
services.AddBareWireSqs(sqs =>
{
    sqs.Region("eu-central-1");
});

// Explicit credentials (local development only)
services.AddBareWireSqs(sqs =>
{
    sqs.UseExplicitCredentials("AKIAIOSFODNN7EXAMPLE", "<secret>");
    sqs.Region("us-east-1");
});

// LocalStack (http allowed via opt-in)
services.AddBareWireSqs(sqs =>
{
    sqs.ServiceUrl("http://localhost:4566");
    sqs.AllowInsecureEndpoint(); // SEC-01 opt-out for test environments
    sqs.Region("us-east-1");
});
```

### Available options

| Configurator method             | Default  | Description                                                  |
|---------------------------------|----------|--------------------------------------------------------------|
| `UseDefaultCredentials()`       | *(default)* | AWS SDK credential chain (IAM role, env vars, etc.)       |
| `UseExplicitCredentials(k, s)`  | —        | Static Access Key ID + Secret Access Key                    |
| `Region(string)`                | *(env)*  | AWS region name (e.g. `eu-central-1`)                       |
| `ServiceUrl(string)`            | *(AWS)*  | Custom endpoint URL (LocalStack, custom SQS-compatible)     |
| `AllowInsecureEndpoint()`       | false    | Opt out of TLS enforcement (test environments only)         |
| `VisibilityTimeout(TimeSpan)`   | 30 s     | Default message visibility timeout                          |
| `WaitTimeSeconds(int)`          | 20       | SQS long-poll wait time (0–20)                              |
| `MaxNumberOfMessages(int)`      | 10       | Max messages per `ReceiveMessage` call (1–10)               |
| `MaxInFlightMessages(int)`      | 100      | Max concurrent in-flight messages in registry               |

## Capabilities

```
TransportCapabilities.NativeDeduplication | DlqNative | BatchReceive
```

- **NativeDeduplication** — FIFO queues support `MessageGroupId`-based ordering and content-based deduplication. Full BareWire-level `MessageGroupId`/`MessageDeduplicationId` mapping implemented in R4.2 — see [FIFO (R4.2)](#fifo-r42) section.
- **DlqNative** — Exhausted messages are routed to a DLQ automatically via `RedrivePolicy`. Configure `bw.sqs.max-receive-count` on your `QueueDeclaration`.
- **BatchReceive** — `ReceiveMessage` retrieves up to 10 messages per call.

## Settlement semantics (ADR-014)

| BareWire action | SQS operation                          |
|----------------|----------------------------------------|
| `Ack`          | `DeleteMessage` — permanent removal    |
| `Nack`         | `ChangeMessageVisibility(0)` — immediate redelivery |
| `Requeue`      | `ChangeMessageVisibility(0)` — same as Nack |
| `Defer`        | `ChangeMessageVisibility(0)` — release for later redelivery |
| `Reject`       | No operation — message remains; `RedrivePolicy` moves it to DLQ after `maxReceiveCount` exhausted |

**Why Reject does not call `DeleteMessage`:** SQS has no native dead-letter API. Deleting the message would silently discard it without triggering the DLQ. See ADR-014 for the full rationale.

## Topology arguments

Topology is declared via `QueueDeclaration.Arguments`:

```csharp
var topology = new TopologyDeclaration
{
    Queues =
    [
        new QueueDeclaration("my-queue", Arguments: new Dictionary<string, object>
        {
            ["bw.sqs.visibility-timeout"] = TimeSpan.FromSeconds(60),
            ["bw.sqs.wait-time-seconds"] = 20,
            ["bw.sqs.max-receive-count"] = 5,
        }),
    ],
};
```

| Argument key                           | Type              | Default | Description                                   |
|----------------------------------------|-------------------|---------|-------------------------------------------------|
| `bw.sqs.visibility-timeout`            | `TimeSpan`/string | 30 s    | Queue visibility timeout                        |
| `bw.sqs.wait-time-seconds`             | int (0–20)        | 20      | `ReceiveMessageWaitTimeSeconds`                 |
| `bw.sqs.fifo`                          | bool              | false   | FIFO queue (name must end in `.fifo`)           |
| `bw.sqs.max-receive-count`             | int (≥1)          | 5       | DLQ redrive `maxReceiveCount`                   |
| `bw.sqs.content-based-deduplication`   | bool              | false   | Enable content-based dedup for FIFO queues      |

## FIFO (R4.2)

BareWire fully supports SQS FIFO queues. FIFO fields (`MessageGroupId`, `MessageDeduplicationId`) are set **only** when the target queue name ends with `.fifo` — standard queues are unaffected (backward-compatible).

### MessageGroupId mapping

| Resolution order | Header / source | Notes |
|-----------------|-----------------|-------|
| 1 (explicit)    | `BW-MessageGroupId` header | Set by the producer for explicit group control. |
| 2 (fallback)    | `correlation-id` header (kebab-case) | Populated automatically by `BareWireBus` from `ISagaState.CorrelationId` — delivers per-saga FIFO ordering with no extra configuration. |
| absent          | Guard throws `BareWireTransportException` | FIFO queues require a `MessageGroupId` per batch entry; BareWire fails fast rather than letting SQS return `InvalidParameterValue`. The exception message contains only the queue name and header **names** — never header values (SEC-4). |

**Security note (ADR-015):** `MessageGroupId` is an **ordering** boundary, NOT an authorization or tenant-isolation boundary. A sender can supply any group id they choose. Do not use `BW-MessageGroupId` for access-control decisions. See ADR-015 for the full rationale.

### MessageDeduplicationId

| Resolution order | Source | Notes |
|-----------------|--------|-------|
| 1 (explicit)    | `BW-MessageDeduplicationId` header | Full control; ignored by broker if content-based dedup is enabled at the queue level. |
| 2 (content-based) | Broker (SHA-256 of body, server-side) | When `EnableContentBasedDeduplication = true` / `ContentBasedDeduplication()` is configured. BareWire sends no explicit id. |
| 3 (generated)   | SHA-256 of (`MessageGroupId` + body) → URL-safe Base64 (43 chars) | Deterministic: same (group, body) within 5 minutes → same dedup id. Different groups with the same body → different ids. |

### Content-based deduplication

Enable content-based dedup (requires the queue to have `ContentBasedDeduplication=true`):

```csharp
// Configurator (produce side):
services.AddBareWireSqs(sqs =>
{
    sqs.Region("eu-central-1");
    sqs.ContentBasedDeduplication(); // do not generate explicit MessageDeduplicationId
});

// Topology (queue creation):
new QueueDeclaration("my-orders.fifo", Arguments: new Dictionary<string, object>
{
    ["bw.sqs.fifo"] = true,
    ["bw.sqs.content-based-deduplication"] = true, // sets ContentBasedDeduplication=true on queue
});
```

### Inbound stamping (BW-MessageGroupId / BW-SequenceNumber)

For consumed FIFO messages the following BareWire headers are stamped from **SQS system attributes** (broker-set, not sender-controlled):

| BareWire header | SQS system attribute | Description |
|-----------------|---------------------|-------------|
| `BW-MessageGroupId` | `MessageGroupId` | The FIFO group the message belongs to. |
| `BW-SequenceNumber` | `SequenceNumber` | Monotonic sequence number assigned by the FIFO broker within the group. |

Stamping happens **after** `MapInbound` (which copies sender-supplied `MessageAttributes`). This means a sender cannot spoof `BW-MessageGroupId` via a `MessageAttribute` — the trusted broker value always wins (SEC-3 anti-squatting, consistent with ADR-011 pattern for ASB sessions).

### Consumer ordering note

SQS FIFO guarantees ordering within a `MessageGroupId` at the broker level. BareWire R4.2 does not add a per-group sequential consumer channel (unlike ASB per-session consumers). The long-polling consumer receives messages from a single group sequentially as long as they remain un-settled. Full per-group consumer sequencing is deferred to R4.4.

### Known limitations by phase

### R4.3 (IAM / Encryption — not yet implemented)
- `InstanceProfileCredentialsProvider` for EC2/ECS metadata-service credential refresh.
- SSE-SQS and SSE-KMS encryption at rest. **Note: queues created by R4.1 are NOT encrypted at rest.**
- Full credential rotation strategy.

### R4.4 (Integration tests — not yet implemented)
- LocalStack integration tests in CI (Aspire orchestration).
- Real-queue round-trip, DLQ routing, visibility timeout renewal.

## Security notes

- **Production:** always prefer `UseDefaultCredentials()` with IAM roles over `UseExplicitCredentials()`.
- **TLS:** all requests use HTTPS by default. `AllowInsecureEndpoint()` is for test environments only (SEC-01).
- **Secrets:** `SecretAccessKey` is never logged and never appears in `ToString()` output (SEC-02).
- **Encryption at rest:** not configured in R4.1. Enable SSE-SQS/SSE-KMS via queue policy until R4.3 is available.
