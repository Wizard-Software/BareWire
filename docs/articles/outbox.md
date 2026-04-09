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

## Resilience

If RabbitMQ is unavailable, messages accumulate in the outbox table. The `OutboxDispatcher` retries on each polling interval. Once the broker recovers, all pending messages are dispatched in order.

You can inspect pending messages:

```
GET /outbox/pending   — returns count of undispatched outbox messages
```

> See: `samples/BareWire.Samples.TransactionalOutbox/`
