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

The inbox `ProcessedAt` marker is committed atomically with the consumer's work: inside a `System.Transactions.TransactionScope` on providers that support ambient transactions (PostgreSQL, SQL Server), or inside an explicit local transaction on SQLite (see [SQLite](#sqlite-explicit-local-transaction) below). The middleware pins **one** physical connection for its own inbox/outbox writes, so the common case stays single-connection.

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

### SQLite: explicit local transaction

SQLite has no `System.Transactions` support, so on SQLite the middleware does **not** use a
`TransactionScope`. It opens an explicit local database transaction on the pinned connection instead
(`ReadUncommitted`, which Microsoft.Data.Sqlite turns into a deferred `BEGIN`, so the write lock is taken
only at the first write rather than for the whole handler). The inbox `ProcessedAt` marker and the buffered
outbox messages commit or roll back together, exactly as on the other providers, and no
`ConfigureWarnings(... AmbientTransactionWarning ...)` suppression is needed. Other providers keep using
the `TransactionScope` described above.

A consumer `DbContext` that shares the pinned connection (`IOutboxConnectionAccessor.Current`) **must** also
join the transaction through `IOutboxConnectionAccessor.CurrentTransaction`, because Microsoft.Data.Sqlite
rejects commands on a connection that has an open local transaction the command is not enlisted in:

```csharp
services.AddDbContext<MyConsumerDbContext>((sp, options) =>
{
    DbConnection? shared = sp.GetRequiredService<IOutboxConnectionAccessor>().Current;
    options.UseSqlite(shared ?? new SqliteConnection(connectionString));
});

// In the consumer, before the first write:
var accessor = serviceProvider.GetRequiredService<IOutboxConnectionAccessor>();
if (accessor.CurrentTransaction is { } transaction)
    await myDbContext.Database.UseTransactionAsync(transaction, cancellationToken);
```

> **Breaking change for SQLite users.** A consumer `DbContext` that shares `Current` on SQLite must now
> call `Database.UseTransaction(accessor.CurrentTransaction)` (as above). Previously the shared
> connection had no open transaction, and the warning suppression silently made the commit non-atomic.
> `CurrentTransaction` is `null` on providers that use a `TransactionScope`, so the same code is safe there.

Things to know on SQLite:

- **The transaction belongs to the middleware.** Never call `Commit`, `Rollback` or `Dispose` on
  `CurrentTransaction`, and never call `Database.CommitTransactionAsync()` after `UseTransaction`. If the
  consumer ends the transaction, the middleware throws `InvalidOperationException` instead of silently
  losing the exactly-once guarantee.
- **WAL: start with a write.** In WAL mode a transaction that reads first and then writes can fail with
  `SQLITE_BUSY_SNAPSHOT` if another connection committed in between. Have the consumer perform a write
  before (or without) reading data that another writer may change.
- **Shared cache is not supported** for consume transactions. With `Cache=Shared`, `ReadUncommitted`
  enables `PRAGMA read_uncommitted = 1`, which allows dirty reads between connections that share the cache.
- A consumer that opens its **own** connection to the same SQLite file may wait for the write lock held by
  the pinned connection.
- SQLite remains for development and testing only (`AllowNonAtomicProvider = true`, single dispatcher
  instance) — see Horizontal Scaling below.

> **Do not suppress `AmbientTransactionWarning` on a provider other than SQLite.** On a provider that
> supports ambient transactions, suppressing the warning silently disables atomicity of the consume
> transaction. This is unsupported.

## Publishing from a consumer

When the `TransactionalOutboxMiddleware` is active, every message a consumer publishes or sends is buffered and written to the outbox table **in the same transaction** as the consumer's business changes and the inbox marker. This covers `context.PublishAsync`, `context.GetSendEndpoint(...)`, `context.RespondAsync`, an `IBus` injected into the consumer, and messages published by sagas. Nothing leaves for the broker until the transaction commits; the `OutboxDispatcher` then delivers the buffered messages like any other outbox row.

```csharp
public sealed class TransferConsumer(TransferDbContext db) : IConsumer<TransferRequested>
{
    public async Task ConsumeAsync(ConsumeContext<TransferRequested> context)
    {
        db.Transfers.Add(new Transfer(context.Message.Id));

        // Buffered, not sent: written to the outbox atomically with the Transfer row.
        await context.PublishAsync(new TransferAccepted(context.Message.Id), context.CancellationToken);
    }
}
```

What this means in practice:

- **Handler fails, nothing is published.** If the consumer throws, the transaction rolls back and the buffered messages are discarded together with the business changes.
- **In-process retries start from a clean buffer.** When a retry policy re-runs the handler, messages published by the failed attempt are discarded; only the successful attempt's messages are stored, so a retried message does not publish duplicates.
- **Retries do not undo writes.** The reset clears only the buffered outgoing messages. Business writes made by a failed attempt are not rolled back, so handlers must be idempotent. The in-memory outbox does not reset its buffer between attempts.
- **Delivery is delayed until commit plus polling.** Messages become visible to the broker after the commit and the next dispatcher poll, i.e. up to `PollingInterval` (default 1 second) later. Tune `PollingInterval` and `DispatchBatchSize` if this latency matters.
- **Responses are transactional too.** `RespondAsync` is buffered like any other message and keeps its correlation id, so a request/response caller sees the response after the commit plus up to `PollingInterval`. Size the caller's request timeout accordingly.
- **The buffer is bounded.** A single consume operation may buffer at most `MaxBufferedMessagesPerConsume` messages (default 10000, must be greater than zero). Publishing one more throws a `BareWireException` from the publish call, which fails the consume operation and rolls the transaction back. A second limit, `MaxBufferedBytesPerConsume` (default 64 MiB, must be greater than zero), caps the total body size buffered by one consume operation and fails it the same way. Captured messages bypass the bus's publish byte budget, so these two limits are the only bounds on what a consume operation can buffer.
- **Buffered data is stored in plaintext.** Buffered payloads and headers are written to the outbox table unencrypted until they are dispatched, and are then retained according to `OutboxRetention` (default 7 days). Encrypt sensitive payloads (for example in a serializer or a middleware) if that matters for your data.

```csharp
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options.UseNpgsql(connectionString),
    configureOutbox: outbox =>
    {
        outbox.MaxBufferedMessagesPerConsume = 1_000;
        outbox.MaxBufferedBytesPerConsume = 16 * 1024 * 1024;
    });
```

### Work that outlives the handler

The buffer is closed as soon as the consume operation persists it. A message published afterwards from work that outlives the handler (for example a fire-and-forget `Task.Run` started by the consumer) cannot join the transaction: it is sent **directly to the transport, non-transactionally**, and a warning is logged with the message id and type. Await such work inside the handler if you need its messages to be transactional.

### What is not intercepted

The following paths do not go through the outbox buffer and are sent as before:

- **Request-client requests.** The RabbitMQ request client publishes on its own channel, immediately and outside the transaction, even if the consume transaction later rolls back.
- **Scheduled and timeout messages** (delayed delivery, saga timeouts).
- **Kafka retry and dead-letter producer** messages.

### Assumptions

- **One bus per process.** The active outbox buffer is tracked per process, so the interception assumes a single BareWire bus in the process.
- **Single interceptor slot.** The outbox registers its interceptor with `AddBareWireOutbox`, replacing any `IOutboundMessageInterceptor` registered earlier. A custom `IOutboundMessageInterceptor` registered **after** `AddBareWireOutbox` replaces the outbox one, which turns this behavior off; a warning is logged at startup when more than one is registered.

## Horizontal Scaling

When you run more than one instance of the dispatcher (multiple pods/processes), each `GetPendingAsync` poll **atomically claims** its batch so two instances never pick the same rows. On PostgreSQL the claim uses `FOR UPDATE SKIP LOCKED`; a claimed row carries a `LockedAt`/`LockedBy` marker and is invisible to other instances until the claim expires.

- **Claim expiry (`OutboxLockTimeout`, default 30s):** if an instance crashes between claiming and publishing, its rows become re-claimable by another instance once `OutboxLockTimeout` elapses — no message is lost. An expired claim is not counted as a retry: it does not increase the row's `RetryCount` (see [Retries after a transport rejection](#retries-after-a-transport-rejection)). Set it conservatively above your broker's worst-case publish-confirm time; it is validated to be at least `3 × PollingInterval`.
- **Delivery guarantee:** each row is claimed by exactly one instance per cycle (exactly-once-claim), but end-to-end delivery remains **at-least-once** — keep consumers idempotent (the inbox handles this).
- **Ordering:** with parallel instances claiming disjoint batches, global send order across instances is **not** guaranteed. If you need ordered delivery, run a single dispatcher instance, or partition by key. Set `OrderingMode.PerKey` (with `OrderingKeyHeaderName`) to guarantee head-of-line ordering per key group at dispatch time; pair it with a consumer endpoint using `OrderedBy`/`OrderedByHeader` on the **same header name** to preserve order end-to-end. See [Per-Key Consumer Ordering](per-key-ordering.md). With `OrderingMode.PerKey`, a dialect without a head-of-line predicate (or `AllowDegradedOrdering`) can put several rows of one key into one batch; a sibling the broker already accepted behind a rejected head is released without being marked delivered and is sent again later — another duplicate source the inbox suppresses.
- **Provider note:** the atomic claim requires PostgreSQL. SQLite is for testing/development only and is not suitable for multi-instance production use. Other providers can supply a custom `IOutboxSqlDialect`.

## Retries after a transport rejection

When the transport reports a publication as **not confirmed** (a broker nack, or an unroutable message under guaranteed routing), the dispatcher does not mark the row delivered. It releases the row back to the table with a deferral, so the row is retried later instead of on the very next poll. This applies to every transport. There are no new options: every value below is derived from `PollingInterval`, `DispatchBatchSize` and `OutboxLockTimeout`.

### Deferral with escalation

The first rejection defers the row by `PollingInterval`. Each further rejection doubles the previous deferral, up to `OutboxLockTimeout`. At the defaults (`PollingInterval` = 1 s, `OutboxLockTimeout` = 30 s):

| Rejections so far | 0 | 1 | 2 | 3 | 4 | 5 or more |
|-------------------|---|---|---|---|---|-----------|
| Base deferral     | 1 s | 2 s | 4 s | 8 s | 16 s | 30 s |

- **Jitter.** Up to +20% is added on top of the base deferral. The deferral never drops below `PollingInterval` and never exceeds about 1.2 × `OutboxLockTimeout` (36 s at the defaults). Rows rejected together are spread over four jitter bands (by row id), so they do not all come back in one wave. On the first rejections, where the base deferral is only `PollingInterval`, the spread is smaller than one polling step.
- **What counts as a rejection.** A row's `RetryCount` grows only when the transport rejects it. An expired claim after a process crash, or a batch whose send threw, does not increase it.
- **No retry limit.** There is no maximum retry count and no dead-letter step. A row the transport rejects every time stays in the outbox table (cleanup removes only delivered rows) and, at the defaults, costs one send plus a few database writes roughly every 30–37 seconds, indefinitely. Watch `barewire.outbox.rows.retried` (below), then fix or delete such rows by hand.

### Fair batch claiming

Each poll splits its batch between **new** rows and **due retries** (deferred rows whose deferral has elapsed, plus claims abandoned by a crashed instance), so a cohort of permanently rejected rows cannot starve new messages:

- **Retry reserve.** Due retries are guaranteed at least `max(1, n / 4)` slots per cycle (integer division), where `n` is `DispatchBatchSize` minus the rows this instance still holds from a previous cycle. For `n` of 4 or more, new rows therefore keep at least 75% of the batch (with `n` of 2 or 3 the single reserved slot is a larger share): at the default batch size of 100, the split is 75 new / 25 retries **when both classes are waiting**.
- **Unused capacity flows both ways.** When there are fewer new rows than their share, due retries may fill the rest of the batch; when there are fewer due retries than the reserve, new rows take the unused slots. Without a retry backlog, new rows get the whole batch.
- **Batch size 1.** With `DispatchBatchSize = 1`, the single slot takes turns between the two classes across cycles, so neither class is starved while both are waiting.

### Bounded retry delay and observability

Due retries are claimed in **due-time order** (the row whose deferral elapsed first goes first), so a retry waits a bounded time after it becomes due. That bound grows only when the retries that became due earlier — including a large cohort of permanently rejected rows — exceed the retry reserve of each cycle; with a very large rejected backlog, a due retry can wait several cycles. Two instruments make this visible:

| Instrument | Type | Unit | Meaning |
|------------|------|------|---------|
| `barewire.outbox.rows.retried` | counter | `{row}` | Rows released for a deferred retry after a transport rejection. Counts rejections only — not expired claims, not rows held back by per-key ordering, not batches whose send threw. Reported per instance: sum across instances. |
| `barewire.outbox.retry.oldest_due_age` | observable gauge | `s` | Age of the oldest retry that is due but not yet claimed (an elapsed deferral or an abandoned claim); `0` when nothing is due. Every instance reports the same table-wide value: aggregate with `max`, not `sum`. |

- Both instruments live on the `BareWire` meter and carry no tags (row ids are never used as tag values). They are created when an `IMeterFactory` is registered in DI — the .NET generic host registers one by default.
- The gauge is sampled lazily by the dispatcher loop after a metrics collection, so it reports no value before the first sample and can lag by about one collection interval. The sample costs one query per collection interval per instance, and only while the gauge is observed; on the built-in PostgreSQL dialect that query reads a single row through the claim index, while other providers (such as SQLite) read the lock time of every undelivered row that carries one.
- Each dispatcher also logs a rate-limited **Warning** (at most one per minute) with the id of one representative rejected row, its `RetryCount`, how many rows of the batch were released for retry, and how many were suppressed since the previous entry. It never includes the instance identity, the message body or the headers.

### Multi-instance requirements

A deferred row is written with a `LockedAt` value computed from the **releasing** instance's clock and its `OutboxLockTimeout`; whether a row is claimable is decided from the **claiming** instance's clock and its `OutboxLockTimeout`. Every instance that shares an outbox table must therefore:

- **keep its clock synchronized** (NTP or an equivalent time service), and
- **use the same `OutboxLockTimeout`.**

Violating either has two effects. For a **deferred** row, the retry simply happens earlier or later than scheduled and due-time ordering weakens — no duplicate. For a row that is **still being sent**, clock skew or a shorter `OutboxLockTimeout` on another instance can make its live claim look expired before the publication is confirmed; if that exceeds the margin between `OutboxLockTimeout` and your real publish-confirm time, the other instance re-claims the row and sends it a second time. Delivery stays at-least-once, and a consumer-side [inbox](inbox.md) — with an `InboxRetention` longer than the redelivery window — suppresses the duplicate; but only if the requirements above hold do you avoid producing it.

### Custom SQL dialects

The built-in PostgreSQL dialect claims due retries in due-time order. A custom `IOutboxSqlDialect` whose `ProviderName` matches your provider keeps **correctness and fairness for new rows** — the retry reserve and the new-row share apply unchanged — but its retries are claimed through the public claim statement in row-id order, so the **retry progress guarantee** (due-time order, bounded retry delay) does not apply. BareWire does not validate custom claim SQL: the dialect author is responsible for its correctness, including the per-key head-of-line predicate for `OrderingMode.PerKey`; the startup checks only catch accidental misconfiguration. Return the claim SQL as a parameterized `FormattableString` and never concatenate the instance id or any other value into the SQL text.

### Draining vs backpressure

After a batch, the dispatcher claims the next one immediately — without waiting a `PollingInterval` — only when the batch came back full, at least half of it was confirmed, and the current streak is still short: up to 10 immediate follow-up batches, or 5 once the streak has seen a rejection. After that, or when a send throws, it pauses for one `PollingInterval`, measured from the end of the last batch.

This is a deliberate trade-off. The loop keeps draining while only a **minority** of a batch is rejected, because rejected rows are already deferred one by one and cannot be hot-retried by the next batch — so a backlog of healthy messages is not held hostage by a few failing ones. The price is weaker backpressure: a struggling broker still receives bursts of up to 6 back-to-back batches (the first batch plus 5 follow-ups). When a **majority** of a batch is rejected, the loop always pauses.

## Resilience

If RabbitMQ is unavailable, messages accumulate in the outbox table. Two failure paths are handled differently:

- **The send fails outright** (broker down, connection lost — the transport throws): the instance keeps its claim on the batch, pauses for one `PollingInterval`, and sends the same rows again on the next cycle. This is not a deferral and does not increase a row's `RetryCount`.
- **The transport rejects individual publications** (it reports them as not confirmed): each rejected row is released for a **deferred** retry whose delay escalates with every rejection — see [Retries after a transport rejection](#retries-after-a-transport-rejection).

Once the broker recovers, the pending backlog is dispatched (oldest first within each instance's claimed batch).

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
> and the row is released for a deferred retry (see
> [Retries after a transport rejection](#retries-after-a-transport-rejection)). See
> [Routing semantics](transport-rabbitmq.md#routing-semantics).

> See: `samples/BareWire.Samples.TransactionalOutbox/`
