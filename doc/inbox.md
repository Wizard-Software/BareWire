# Inbox Deduplication

The inbox pattern prevents duplicate message processing when the broker redelivers a message — for example after a consumer crash, network timeout, or explicit NACK. BareWire's inbox is built into the `TransactionalOutboxMiddleware` and shares the same database and transaction as the outbox.

## How It Works

1. A message arrives on a receive endpoint
2. The middleware checks whether `(MessageId, ConsumerType)` already exists in the inbox table
3. If the entry exists and is marked as processed — the message is skipped (duplicate)
4. If no entry exists — a lock row is inserted with an expiry time
5. The consumer processes the message inside a `TransactionScope`
6. On success, the entry is marked as processed — permanently preventing reprocessing

### Composite Key

The inbox key is `(MessageId, ConsumerType)`. This means:

- The **same message** can be processed by **different consumers** independently (e.g. `EmailConsumer` and `AuditConsumer` both process `PaymentReceived`)
- The **same consumer** will never process the **same message** twice on the same endpoint

`ConsumerType` is derived from the endpoint name, the `BW-MessageType` header, or the consumer class name as a fallback.

### Two-Phase Lock

1. **Lock** — on first receipt the inbox creates an entry with `ExpiresAt = now + InboxLockTimeout`. If an entry already exists and has not expired, the message is rejected.
2. **Mark processed** — after the consumer completes successfully, the middleware sets `ProcessedAt`. Entries with `ProcessedAt != null` never allow reprocessing, even after `ExpiresAt` has elapsed.

If a consumer crashes before marking processed and the lock expires, the broker can redeliver the message and a new lock will be acquired — providing at-least-once semantics with deduplication.

## Configuration

The inbox is configured through the same `AddBareWireOutbox` call as the outbox:

```csharp
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options.UseNpgsql(connectionString),
    configureOutbox: outbox =>
    {
        outbox.InboxLockTimeout = TimeSpan.FromSeconds(30);   // default: 30s
        outbox.InboxRetention = TimeSpan.FromDays(7);         // default: 7 days
        outbox.CleanupInterval = TimeSpan.FromHours(1);       // default: 1 hour
    });
```

- **InboxLockTimeout** — how long a processing lock is held before it expires. Set this higher than your longest expected consumer execution time.
- **InboxRetention** — how long processed entries are kept before the cleanup service purges them. Must be longer than the maximum broker redelivery window.
- **CleanupInterval** — how often the background service removes old inbox entries.

## Preserving MessageId on Re-Publish

For inbox deduplication to work across re-publishes of the same logical message, pass the original `MessageId` as a header:

```csharp
var headers = new Dictionary<string, string>
{
    ["message-id"] = originalMessageId.ToString()
};
await bus.PublishAsync(message, headers, ct);
```

Without this, each `PublishAsync` call generates a new `MessageId` and the inbox treats the message as new.

## Multiple Consumers, Same Message

A common pattern is to have multiple consumers react to the same event — each with its own inbox entry:

```csharp
// Both consumers receive PaymentReceived independently
rmq.ReceiveEndpoint("email-notifications", e =>
{
    e.Consumer<EmailNotificationConsumer, PaymentReceived>();
});

rmq.ReceiveEndpoint("audit-log", e =>
{
    e.Consumer<AuditLogConsumer, PaymentReceived>();
});
```

If the broker redelivers a message, each consumer's inbox entry is checked independently. `EmailNotificationConsumer` might have already processed it while `AuditLogConsumer` has not — the inbox handles both correctly.

## Inspecting the Inbox

The InboxDeduplication sample exposes an endpoint to inspect inbox state:

```
GET /inbox   — returns inbox entries with (MessageId, ConsumerType, ProcessedAt)
```

> See: `samples/BareWire.Samples.InboxDeduplication/`
