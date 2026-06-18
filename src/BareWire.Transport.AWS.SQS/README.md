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

- **NativeDeduplication** — FIFO queues support content-based deduplication. Full BareWire-level `MessageGroupId`/`MessageDeduplicationId` mapping arrives in **R4.2**.
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

| Argument key                    | Type              | Default | Description                             |
|---------------------------------|-------------------|---------|-----------------------------------------|
| `bw.sqs.visibility-timeout`     | `TimeSpan`/string | 30 s    | Queue visibility timeout                |
| `bw.sqs.wait-time-seconds`      | int (0–20)        | 20      | `ReceiveMessageWaitTimeSeconds`         |
| `bw.sqs.fifo`                   | bool              | false   | FIFO queue (name must end in `.fifo`)   |
| `bw.sqs.max-receive-count`      | int (≥1)          | 5       | DLQ redrive `maxReceiveCount`           |

## Known limitations by phase

### R4.2 (FIFO — not yet implemented)
- `MessageGroupId` from BareWire headers → `MessageGroupId` in SQS FIFO batch entry.
- `MessageDeduplicationId` for exactly-once within the 5-minute dedup window.
- Ordering guarantees per group.

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
