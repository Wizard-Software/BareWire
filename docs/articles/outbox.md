# Transactional Outbox

The transactional outbox pattern ensures exactly-once message delivery by writing business data and outbox messages in a single database transaction.

## How It Works

1. Your code writes a business entity and an outbox message in one `SaveChangesAsync()` call
2. The `OutboxDispatcher` background service polls the database and publishes pending messages to RabbitMQ
3. On the consumer side, the `TransactionalOutboxMiddleware` provides inbox deduplication to prevent duplicate processing
4. The `OutboxCleanupService` purges delivered outbox records

## Configuration

```csharp
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options.UseNpgsql(connectionString),
    configureOutbox: outbox =>
    {
        outbox.PollingInterval = TimeSpan.FromSeconds(1);
        outbox.DispatchBatchSize = 100;
        outbox.OutboxLockTimeout = TimeSpan.FromSeconds(30); // must be >= 3 x PollingInterval
    });
```

## Publishing with the Outbox

Write business data and the outbox message atomically:

```csharp
app.MapPost("/transfers", async (
    TransferRequest request,
    TransferDbContext db,
    CancellationToken ct) =>
{
    var transfer = new Transfer
    {
        Id = Guid.NewGuid(),
        FromAccount = request.FromAccount,
        ToAccount = request.ToAccount,
        Amount = request.Amount,
        Status = "Pending",
        CreatedAt = DateTime.UtcNow
    };

    db.Transfers.Add(transfer);

    // Outbox message written in the same transaction
    db.OutboxMessages.Add(new OutboxMessage
    {
        Id = Guid.NewGuid(),
        MessageType = typeof(TransferInitiated).FullName!,
        Payload = JsonSerializer.Serialize(new TransferInitiated(transfer.Id, ...)),
        CreatedAt = DateTime.UtcNow
    });

    await db.SaveChangesAsync(ct);  // single atomic transaction

    return Results.Accepted(value: new { transfer.Id });
});
```

## Inbox Deduplication

The `TransactionalOutboxMiddleware` automatically deduplicates messages on the consumer side using a two-phase lock mechanism. The composite inbox key is `(MessageId, ConsumerType)` — the same message can be processed by different consumers independently, but the same consumer will never process it twice.

> See: [Inbox Deduplication](inbox.md) for full details on configuration, composite keys, and multi-consumer patterns

## Horizontal Scaling

When you run more than one instance of the dispatcher (multiple pods/processes), each `GetPendingAsync` poll **atomically claims** its batch so two instances never pick the same rows. On PostgreSQL the claim uses `FOR UPDATE SKIP LOCKED`; a claimed row carries a `LockedAt`/`LockedBy` marker and is invisible to other instances until the claim expires.

- **Claim expiry (`OutboxLockTimeout`, default 30s):** if an instance crashes between claiming and publishing, its rows become re-claimable by another instance once `OutboxLockTimeout` elapses — no message is lost. Set it conservatively above your broker's worst-case publish-confirm time; it is validated to be at least `3 × PollingInterval`.
- **Delivery guarantee:** each row is claimed by exactly one instance per cycle (exactly-once-claim), but end-to-end delivery remains **at-least-once** — keep consumers idempotent (the inbox handles this).
- **Ordering:** with parallel instances claiming disjoint batches, global send order across instances is **not** guaranteed. If you need ordered delivery, run a single dispatcher instance, or partition by key. Set `OrderingMode.PerKey` (with `OrderingKeyHeaderName`) to guarantee head-of-line ordering per key group at dispatch time; pair it with a consumer endpoint using `OrderedBy`/`OrderedByHeader` on the **same header name** to preserve order end-to-end. See [Per-Key Consumer Ordering](per-key-ordering.md).
- **Provider note:** the atomic claim requires PostgreSQL. SQLite is for testing/development only and is not suitable for multi-instance production use. Other providers can supply a custom `IOutboxSqlDialect`.

## Resilience

If RabbitMQ is unavailable, messages accumulate in the outbox table. The `OutboxDispatcher` retries on each polling interval. Once the broker recovers, the pending backlog is dispatched (oldest first within each instance's claimed batch).

You can inspect pending messages:

```
GET /outbox/pending   — returns count of undispatched outbox messages
```

> See: `samples/BareWire.Samples.TransactionalOutbox/`
