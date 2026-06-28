# Transactional Outbox

The transactional outbox pattern gives **effectively-once** delivery: business data and the outbox message are written in a single database transaction (no message is ever lost or published without its data), the dispatcher then delivers **at-least-once**, and consumer-side inbox deduplication suppresses the duplicates. (See the *Delivery guarantee* note under [Horizontal Scaling](#horizontal-scaling) and [Inbox Deduplication](inbox.md).)

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

## Consumer Business Writes: Single-Commit vs 2PC

The inbox `ProcessedAt` marker is committed inside a `System.Transactions.TransactionScope`, atomically with the consumer's work. The middleware pins **one** physical connection for its own inbox/outbox writes, so the common case stays single-connection.

A frequent pattern, though, is for the **consumer to also persist business state through its own `DbContext`** inside that same transaction (e.g. a `TransferConsumer` that updates the `Transfer` row). How that second write enlists decides whether the commit is one phase or two:

- **Two physical connections → two-phase (prepared) commit.** If the consumer's `DbContext` opens its own connection, `TransactionScope` enlists two resources and escalates to a 2PC. PostgreSQL ships with `max_prepared_transactions = 0` (prepared transactions disabled), so the consume aborts with `55000: prepared transactions are disabled` and the message is retried until dead-lettered. Enabling it (start Postgres with `-c max_prepared_transactions=100`) makes it work, but a prepared commit is also **slower** — an extra `PREPARE` / `COMMIT PREPARED` round-trip and fsync per message.
- **One shared connection → single-phase commit (recommended).** Have the consumer's `DbContext` reuse the **same** connection the middleware already pinned for the in-flight message, exposed via `IOutboxConnectionAccessor`. One physical connection enlists exactly once, so the business write, the buffered outbox messages, and the inbox marker all commit in a single local transaction — **faster**, and with **no** `max_prepared_transactions` requirement.

Wire the consumer's `DbContext` to prefer the shared connection, falling back to a standalone connection outside a consume operation (startup schema creation, HTTP request handlers, background jobs):

```csharp
services.AddDbContext<TransferDbContext>((sp, options) =>
{
    // System.Data.Common.DbConnection — non-null only while the outbox middleware is
    // processing a message on the current async flow; null on startup / HTTP / background paths.
    DbConnection? shared = sp.GetRequiredService<IOutboxConnectionAccessor>().Current;
    if (shared is not null)
        options.UseNpgsql(shared);            // share the outbox connection → single-phase commit
    else
        options.UseNpgsql(connectionString);  // standalone connection
});
```

> Use the `(IServiceProvider, DbContextOptionsBuilder)` overload so EF builds the options **per scope** — each per-message consumer scope binds to the live pinned connection. The consumer keeps calling `SaveChangesAsync()` as usual; running inside the middleware's `TransactionScope`, its write commits atomically with the outbox and inbox writes — now as one single-phase commit.

> See: `samples/BareWire.Samples.TransactionalOutbox/`, `samples/BareWire.Samples.OrderedConsumers/`, and `samples/BareWire.Samples.InboxDeduplication/` — every sample whose consumer persists business state uses this single-commit pattern, so none requires 2PC.

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

> **Topology drift and at-least-once.** The outbox marks a row delivered only when the transport
> reports the publication confirmed. By default the RabbitMQ transport reports a publication the
> broker *accepted but could not route* (missing binding/queue, wrong routing key) as confirmed — so
> an unroutable outbox message would be marked delivered and removed though no consumer ever saw it.
> To keep the at-least-once guarantee against topology drift, enable guaranteed routing on the
> transport (`rmq.GuaranteedRouting()`): an unroutable publication is then reported as not confirmed,
> the outbox leaves the row claimed, and it is retried. See
> [Routing semantics](transport-rabbitmq.md#routing-semantics).

> See: `samples/BareWire.Samples.TransactionalOutbox/`
