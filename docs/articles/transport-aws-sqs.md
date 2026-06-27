# AWS SQS Transport

The Amazon SQS transport (`BareWire.Transport.AWS.SQS`) implements BareWire's `ITransportAdapter`
on top of Amazon Simple Queue Service. It pairs a long-polling consumer with a batched producer
(up to 10 messages per SQS batch), tracks in-flight messages with credit-based flow control, and
dead-letters via SQS's native `RedrivePolicy`. FIFO queues, IAM instance-profile auth, and SSE
encryption at rest are all supported.

## Registration

The ergonomic path is the bundle package `BareWire.AWS.SQS`, which registers the core engine and
the SQS transport in one call via `AddBareWireWithSqs`. The `bus` delegate is optional — omit it
when transport defaults are enough:

```csharp
builder.Services.AddBareWireWithSqs(
    transport => transport.Region("eu-central-1"),
    bus =>
    {
        bus.AddConsumer<OrderConsumer>();
        // endpoints, middleware, serializers...
    });
```

`AddBareWireWithSqs` is sugar over the explicit two-call form, which remains fully supported (use
it when you register more than one transport, or reference the core and transport packages
separately):

```csharp
builder.Services.AddBareWireSqs(transport => transport.Region("eu-central-1"));
builder.Services.AddBareWire(bus => bus.AddConsumer<OrderConsumer>());
```

## Authentication

The transport supports three credential modes through `ISqsConfigurator`. Prefer the default
credential chain or an instance profile in production — both keep secrets out of application
configuration. The Secret Access Key is never logged and never appears in diagnostic output.

```csharp
services.AddBareWireSqs(sqs =>
{
    sqs.Region("eu-central-1");

    // DefaultChain (recommended, default) — IAM role, env vars, shared credentials file:
    sqs.UseDefaultCredentials();

    // IAM instance profile (EC2 instance profile / ECS task role), credentials from IMDS:
    // sqs.UseInstanceProfileCredentials();          // default role attached to the profile
    // sqs.UseInstanceProfileCredentials("MyAppRole"); // or an explicit role name

    // Explicit static credentials (local development only):
    // sqs.UseExplicitCredentials("AKIAIOSFODNN7EXAMPLE", "<secret>");
});
```

For LocalStack or another SQS-compatible endpoint, point the adapter at a custom URL with
`ServiceUrl`. Plain `http` is rejected unless you also call `AllowInsecureEndpoint()` (test
environments only):

```csharp
services.AddBareWireSqs(sqs =>
{
    sqs.ServiceUrl("http://localhost:4566");
    sqs.AllowInsecureEndpoint();  // opt out of TLS enforcement — test only
    sqs.Region("us-east-1");
});
```

## Long-polling consumer

The consumer uses SQS long polling to minimise empty-receive calls. `WaitTimeSeconds` (0–20,
default 20) sets the poll duration, `MaxNumberOfMessages` (1–10, default 10) the batch size per
`ReceiveMessage` call, `VisibilityTimeout` (default 30 s) the window before an unsettled message
becomes visible again, and `MaxInFlightMessages` (default 100) bounds the consumed-but-unsettled
messages tracked in the in-flight registry. All four are set on `ISqsConfigurator` (see
[Configurator options](#configurator-options)).

## Batching

The producer groups outgoing messages into SQS send batches of up to 10, and the consumer
retrieves up to 10 messages per `ReceiveMessage` call. The transport advertises this through its
capability flags: `NativeDeduplication | DlqNative | BatchReceive`.

## FIFO queues

FIFO fields are set **only** when the queue name ends in `.fifo`; standard queues are unaffected.
Declare a FIFO queue (and its DLQ redrive) through `QueueDeclaration.Arguments`:

```csharp
new QueueDeclaration("my-orders.fifo", Arguments: new Dictionary<string, object>
{
    ["bw.sqs.fifo"] = true,
    ["bw.sqs.content-based-deduplication"] = true,
    ["bw.sqs.max-receive-count"] = 5,
});
```

**MessageGroupId** (the ordering boundary) resolves from the `BW-MessageGroupId` header, falling
back to the auto-stamped `correlation-id` header — which gives per-saga FIFO ordering with no extra
configuration. A FIFO send with no resolvable group id fails fast with `BareWireTransportException`.
`MessageGroupId` is an ordering boundary only, never an authorization or tenant-isolation boundary.

**MessageDeduplicationId** resolves in order: an explicit `BW-MessageDeduplicationId` header;
content-based dedup when enabled (the broker hashes the body); otherwise a deterministic id from a
SHA-256 hash of (`MessageGroupId` + body). Call `ContentBasedDeduplication()` on the configurator so
BareWire sends no explicit id (the queue must have `ContentBasedDeduplication=true`). On consume,
FIFO messages are stamped with broker-trusted `BW-MessageGroupId` and `BW-SequenceNumber` headers.

## Settlement and dead-lettering

BareWire settlement actions map onto SQS operations as follows:

| BareWire action | SQS operation |
|-----------------|---------------|
| `Ack`     | `DeleteMessage` — permanent removal |
| `Nack` / `Requeue` / `Defer` | `ChangeMessageVisibility(0)` — release for redelivery |
| `Reject`  | No operation — `RedrivePolicy` moves it to the DLQ once `maxReceiveCount` is exhausted |

SQS has no native dead-letter API, so `Reject` deliberately does **not** delete the message — that
would silently discard it without triggering the DLQ. Dead-lettering is a `RedrivePolicy` on the
queue, configured via the `bw.sqs.max-receive-count` argument (default 5).

> See: [Retry and Dead Letter Queues](retry-and-dlq.md) for the general retry/DLQ model.

## Encryption at rest

Server-side encryption is a per-queue, opt-in attribute — a queue without an SSE argument is not
encrypted by BareWire. SSE-SQS (`bw.sqs.sse-managed`) and SSE-KMS (`bw.sqs.kms-master-key-id`, with
optional `bw.sqs.kms-data-key-reuse-period`) are mutually exclusive; setting both throws at deploy
time.

```csharp
// SSE-KMS with a customer CMK
new QueueDeclaration("payments", Arguments: new Dictionary<string, object>
{
    ["bw.sqs.kms-master-key-id"] = "alias/my-cmk",   // key id or ARN
    ["bw.sqs.kms-data-key-reuse-period"] = 300,       // optional, 60–86400
});
```

## Configurator options

| `ISqsConfigurator` method              | Default     | Description |
|----------------------------------------|-------------|-------------|
| `UseDefaultCredentials()`              | *(default)* | AWS SDK default credential chain |
| `UseExplicitCredentials(key, secret)`  | —           | Static Access Key ID + Secret Access Key |
| `UseInstanceProfileCredentials(role?)` | —           | IAM instance profile (EC2 / ECS task role) via IMDS |
| `Region(string)`                       | *(env)*     | AWS region name (e.g. `eu-central-1`) |
| `ServiceUrl(string)`                   | *(AWS)*     | Custom endpoint URL (LocalStack / SQS-compatible) |
| `AllowInsecureEndpoint()`              | false       | Opt out of TLS enforcement (test only) |
| `VisibilityTimeout(TimeSpan)`          | 30 s        | Default message visibility timeout |
| `WaitTimeSeconds(int)`                 | 20          | Long-poll wait time (0–20) |
| `MaxNumberOfMessages(int)`             | 10          | Max messages per `ReceiveMessage` (1–10) |
| `MaxInFlightMessages(int)`             | 100         | Max concurrent in-flight messages |
| `ContentBasedDeduplication()`          | off         | Skip explicit dedup id (broker hashes body) |

## See also

- [API reference](../api/index.md)
- [Configuration](configuration.md)
- [Retry and Dead Letter Queues](retry-and-dlq.md)
