# BareWire.Outbox.EntityFramework

Entity Framework Core provider for BareWire Outbox with SQL Server, PostgreSQL, and SQLite support.

## Installation

```bash
dotnet add package BareWire.Outbox.EntityFramework
```

## Usage

```csharp
services.AddBareWireOutbox(
    options => options.UseNpgsql(connectionString),
    outbox =>
    {
        outbox.PollingInterval = TimeSpan.FromSeconds(1);
        outbox.OutboxLockTimeout = TimeSpan.FromSeconds(30);
        outbox.OutboxRetention = TimeSpan.FromDays(7);
        outbox.AutoCreateSchema = true;
    });
```

## Delivery Guarantees

BareWire Outbox provides **exactly-once-claim per row** within a single polling cycle:
each pending outbox row is claimed by exactly one dispatcher instance at a time.
End-to-end delivery remains **at-least-once** — the consumer side must be idempotent
(use `InboxFilter` / `BareWire.Outbox`'s inbox deduplication to achieve exactly-once processing).
The inbox `ProcessedAt` marker is committed **atomically** within the same transaction as the
consumer's business state and buffered outbox messages, so a crash after the business commit
cannot leave a message reprocessable (see ADR-033).

### PostgreSQL: consumer business writes — single-commit vs 2PC

The atomic commit above uses a `System.Transactions.TransactionScope`. The middleware pins **one**
physical connection for its own inbox/outbox writes, so the common case stays single-connection. But a
frequent pattern is for the **consumer to also persist business state through its own `DbContext`**
inside the same transaction. How that second write enlists decides whether the commit is one phase or
two:

- **Two physical connections → two-phase (prepared) commit.** If the consumer's `DbContext` opens its
  own connection, `TransactionScope` enlists two resources and escalates to a 2PC. PostgreSQL ships with
  `max_prepared_transactions = 0` (prepared transactions disabled), so the consume aborts with
  `55000: prepared transactions are disabled` and the message is retried until dead-lettered. Enabling it
  (start Postgres with `-c max_prepared_transactions=100`) makes it work, but a prepared commit is also
  **slower** — an extra `PREPARE` / `COMMIT PREPARED` round-trip and fsync per message.

- **One shared connection → single-phase commit (recommended).** Have the consumer's `DbContext` use the
  **same** connection the middleware already pinned for the in-flight message, exposed via
  `IOutboxConnectionAccessor`. One physical connection enlists exactly once, so the business write, the
  buffered outbox messages, and the inbox marker all commit in a single local transaction — **faster**,
  and with **no** `max_prepared_transactions` requirement at all.

Wire the consumer's `DbContext` to prefer the shared connection, falling back to a standalone connection
outside a consume operation (startup schema creation, HTTP request handlers, background jobs):

```csharp
services.AddDbContext<MyConsumerDbContext>((sp, options) =>
{
    // System.Data.Common.DbConnection — non-null only while the outbox middleware is processing
    // a message on the current async flow; null on startup / HTTP / background paths.
    DbConnection? shared = sp.GetRequiredService<IOutboxConnectionAccessor>().Current;
    if (shared is not null)
        options.UseNpgsql(shared);            // share the outbox connection → single-phase commit
    else
        options.UseNpgsql(connectionString);  // standalone connection
});
```

> Use the `(IServiceProvider, DbContextOptionsBuilder)` overload so EF builds the options **per scope** —
> each per-message consumer scope then binds to the live pinned connection. The consumer keeps calling
> `SaveChangesAsync()` as usual; because it runs inside the middleware's `TransactionScope`, its write
> commits atomically with the outbox and inbox writes — now as one single-phase commit.

The `OrderedConsumers` sample uses this single-commit pattern and needs no 2PC. Samples that have **not**
adopted it — `TransactionalOutbox`, `InboxDeduplication` — still rely on 2PC, which the Aspire AppHost
enables via `max_prepared_transactions` (see `samples/README.md`).

## Horizontal Scaling and Row Claims

When running multiple dispatcher instances (horizontal scaling), each `GetPendingAsync` call
atomically claims a batch of rows using `FOR UPDATE SKIP LOCKED` on PostgreSQL.
Claimed rows are invisible to other instances until the claim expires.

A row is claimable when:
- `DeliveredAt IS NULL` — the row has not yet been delivered, AND
- `LockedAt IS NULL` (unclaimed) OR `LockedAt < NOW() - OutboxLockTimeout` (lock expired)

### Crash Recovery

If a dispatcher instance crashes between claiming rows and publishing them to the broker,
the claimed rows remain locked with a stale `LockedAt` timestamp. Once `OutboxLockTimeout`
elapses, another healthy dispatcher instance re-claims and re-publishes those rows.

This ensures **no message is permanently lost** due to instance failure.

### Startup Safety

The dispatcher claims and publishes nothing until the host has **fully started**: it gates its first poll
on `IHostApplicationLifetime.ApplicationStarted`, which fires only after every hosted service's `StartAsync`
has completed successfully. If startup aborts (a later hosted service throws, or the host is stopped
mid-startup), `ApplicationStarted` never fires — so an instance that never became healthy publishes no
messages and marks no rows delivered. The startup guards (atomic-provider and, under `PerKey`, the
ordering-dialect check) are registered **before** the dispatcher, so they run — and can fail fast — before
any outbox side effect.

### Polling Cost and Backlog Drain

When the outbox is idle — or the pending backlog is smaller than `DispatchBatchSize` — each polling
cycle issues one claim `UPDATE` per instance (affecting `0..DispatchBatchSize` rows) and then waits
`PollingInterval` before the next cycle. At the default `PollingInterval` of `1s` that is roughly one
write statement per second per instance at steady state. For low-throughput deployments — or when
running many instances — raise `PollingInterval` to reduce steady-state idle write/WAL load on the
shared `OutboxMessages` table.

Under a backlog larger than `DispatchBatchSize` the dispatcher **drains**: as long as each batch is
full and every row is confirmed by the broker, it claims and sends the next batch **immediately**,
without waiting `PollingInterval` — so a single instance is not capped at
`DispatchBatchSize / PollingInterval`. The drain runs in **bounded bursts**: after a fixed internal cap
of consecutive full-and-confirmed batches (currently 10) the loop yields one `PollingInterval` before
resuming. This holds catch-up to roughly `cap × DispatchBatchSize` per `PollingInterval` (≈10× the timer
ceiling at the defaults), so a large backlog cannot become an unbounded tight loop that churns one
broker channel and one claim-`UPDATE` per batch as fast as the process can spin. The first claim after a
nacked, empty, partial, or burst-capped batch *is* paced by `PollingInterval` (so a failing broker is
retried no faster than once per `PollingInterval`, never hammered). In short, `PollingInterval` bounds
the **idle poll rate** and the **retry backoff**, and together with the burst cap bounds the
**catch-up ceiling** — it is not a per-batch throttle during an active drain burst. To further cap
catch-up load against a shared store/broker, keep `DispatchBatchSize` modest and provision for the
recovery burst.

### Nacked Rows (Partial Send Failures)

Rows the broker does not confirm (nacks) are **explicitly released**: the dispatcher clears their
per-instance lock as soon as the batch completes, so they are re-claimed on the **next poll cycle**
(about one `PollingInterval` later) — **not** after `OutboxLockTimeout`. `OutboxLockTimeout` is only the
fallback for a dispatcher that *crashes* mid-send (see [Crash Recovery](#crash-recovery)); a normal nack
never waits for it.

Because a nack retries within ~`PollingInterval`, a persistently failing ("poison") row is re-sent
roughly once per `PollingInterval` per instance, and during a broker outage every pending row nacks and
is released each cycle — so retry load scales with the backlog at the poll cadence. Size
`PollingInterval` for that worst case (and add poison-message handling upstream if a row can fail
indefinitely) rather than assuming a slower `OutboxLockTimeout`-spaced retry.

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `PollingInterval` | `1s` | How often the dispatcher polls for pending rows. |
| `DispatchBatchSize` | `100` | Maximum rows claimed per polling cycle. |
| `OutboxLockTimeout` | `30s` | How long a claim is held before it expires and another instance may re-claim. |
| `OutboxRetention` | `7 days` | How long delivered rows are retained before cleanup removes them. |
| `InboxLockTimeout` | `30s` | How long an inbox deduplication lock is held. |
| `InboxRetention` | `7 days` | How long processed inbox entries are retained. |
| `CleanupInterval` | `1h` | How often the cleanup service removes expired rows. |
| `AutoCreateSchema` | `false` | When `true`, creates tables automatically at startup. |
| `AllowDegradedOrdering` | `false` | When `OrderingMode` is `PerKey` on a dialect without native head-of-line ordering, startup **fails fast**. Set `true` to downgrade to a warning and accept passthrough (possibly out-of-order) delivery. |

### Validation Rules

The following invariants are enforced at startup:

- `OutboxLockTimeout > TimeSpan.Zero`
- `OutboxRetention > OutboxLockTimeout` — prevents stale locks surviving the cleanup window
- `OutboxLockTimeout >= 3 * PollingInterval` — the lock must survive at least one full
  poll-publish-confirm cycle; setting it too low re-introduces duplicate delivery

## Schema — Existing Databases

The repository uses `IRelationalDatabaseCreator.CreateTablesAsync` for fresh databases
(no EF migrations). For existing databases, apply the following `ALTER TABLE` manually:

```sql
ALTER TABLE "OutboxMessages" ADD COLUMN "LockedAt" timestamptz NULL;
ALTER TABLE "OutboxMessages" ADD COLUMN "LockedBy" varchar(256) NULL;

-- PostgreSQL only (partial index for claim query performance):
CREATE INDEX IF NOT EXISTS "IX_OutboxMessages_Claim"
  ON "OutboxMessages" ("DeliveredAt", "LockedAt", "Id")
  WHERE "DeliveredAt" IS NULL;

-- All providers (supports the post-claim SELECT: WHERE "LockedBy" = ? AND "DeliveredAt" IS NULL):
CREATE INDEX IF NOT EXISTS "IX_OutboxMessages_LockedBy"
  ON "OutboxMessages" ("LockedBy", "DeliveredAt", "Id");
```

For SQLite (testing/development), a composite index without the partial filter is used:

```sql
CREATE INDEX IF NOT EXISTS "IX_OutboxMessages_Claim"
  ON "OutboxMessages" ("DeliveredAt", "LockedAt", "Id");
CREATE INDEX IF NOT EXISTS "IX_OutboxMessages_LockedBy"
  ON "OutboxMessages" ("LockedBy", "DeliveredAt", "Id");
```

## Ordering (PerKey)

By default, outbox rows are dispatched in arbitrary order (`OrderingMode.None`).
The `OrderingMode.PerKey` mode enforces **head-of-line ordering per key**: within a key group,
only the oldest undelivered row may be claimed in each polling cycle.
Rows without a key (`OrderingKey IS NULL`) always pass through in parallel without ordering.

### Configuration

```csharp
services.AddBareWireOutbox(
    options => options.UseNpgsql(connectionString),
    outbox =>
    {
        outbox.OrderingMode = OrderingMode.PerKey;

        // REQUIRED when PerKey is active — no implicit default.
        // Choose a header that is stable per aggregate/stream (e.g. aggregate-id).
        // Omitting this throws BareWireConfigurationException at startup.
        outbox.OrderingKeyHeaderName = "aggregate-id";
    });
```

`OrderingKeyHeaderName` must be set explicitly — there is no default value.
At startup, if `OrderingMode` is `PerKey` and `OrderingKeyHeaderName` is null, empty, or
whitespace, `BareWireConfigurationException` is thrown.
Keys longer than 256 characters are stored as keyless (`NULL`) — they are never truncated, because
truncation would silently merge distinct long keys into a single head-of-line group.

### Choosing a key header

`correlation-id` is only an example, not the recommended default.
It orders correctly **only when it is stable per aggregate** and the same value is set on every
message in the stream.
When `correlation-id` is unique per message (the common case for request correlation), each row
forms its own group: ordering adds no benefit and the `IX_OutboxMessages_Ordering` index grows
to one entry per row (maximum size and write amplification, zero gain).

### Additive migration for existing databases

When enabling `PerKey` on a database that already has the outbox schema, apply the following
additive, no-backfill DDL.
The new `OrderingKey` column is `NULL`-able, so existing rows remain valid (they become keyless /
passthrough) — no downtime or backfill is required.

```sql
-- Add the nullable OrderingKey column (existing rows become keyless, no backfill needed):
ALTER TABLE "OutboxMessages" ADD COLUMN "OrderingKey" varchar(256) NULL;

-- PostgreSQL only — partial index for the NOT EXISTS claim predicate performance:
CREATE INDEX IF NOT EXISTS "IX_OutboxMessages_Ordering"
  ON "OutboxMessages" ("OrderingKey", "Id")
  WHERE "DeliveredAt" IS NULL;
```

For SQLite (testing/development) no additional index is needed for `PerKey` — the store applies
a client-side head-of-line filter before the `LIMIT`.

### Per-transport end-to-end condition

The outbox guarantees only **ordered hand-off to the broker per key**.
End-to-end ordering at the consumer requires **additional transport configuration**:

| Transport | Required configuration |
|-----------|----------------------|
| Kafka | Route messages with the same key to the same partition (partition key = ordering key). |
| SQS FIFO | Set `MessageGroupId` = ordering key. |
| Azure Service Bus | Use sessions (`SessionId` = ordering key). |
| Google Pub/Sub | Enable `enable_message_ordering`; set `ordering_key` on each message. |
| RabbitMQ | Single-active-consumer per queue, or careful requeue policy. |

A transport that does not declare `TransportCapabilities.OrderingKeys` (or the equivalent native
ordering primitive) will **not** honor end-to-end order even when the outbox emits rows in order.

### Custom SQL dialects and PerKey

If you implement a custom `IOutboxSqlDialect` (e.g. for SQL Server) and want `PerKey` ordering, you must
do **two** things: (1) **override the 5-argument `GetClaimSql` overload** to emit a head-of-line predicate
for `PerKey`, and (2) **declare the capability** by returning `true` from
`SupportsPerKeyHeadOfLineOrdering`. The default interface implementation delegates to the 4-argument
overload (passthrough — no ordering) and the capability flag defaults to `false`. When `PerKey` is active,
that dialect is the **active claim path** (its `ProviderName` matches the active EF Core provider), and it
does not declare `SupportsPerKeyHeadOfLineOrdering`, startup **fails fast** with a
`BareWireConfigurationException` so messages are never silently delivered out of order.

The guard is **provider-aware**: it only enforces the capability when your dialect actually runs. If the
active provider has *no* matching dialect — for example SQLite single-instance with
`AllowNonAtomicProvider = true` — the store uses the client-side fallback claim, which enforces head-of-line
ordering itself (it filters to each key's oldest undelivered row before the `LIMIT`). `PerKey` therefore
starts cleanly on a fallback provider with no dialect capability and **without** `AllowDegradedOrdering`; the
capability requirement applies only to the dialect on the path that actually claims rows.

Capability is an **explicit declaration**, never inferred from the SQL text: a dialect whose claim SQL
merely *differs* from the `None` variant — but still lacks a real head-of-line predicate — is rejected
unless it declares the flag. (A SQL-text diff would be cosmetic and could give false confidence on this
public extension boundary.) Conversely, declaring the flag is **not** a bypass either: a dialect that
declares `SupportsPerKeyHeadOfLineOrdering` but whose `PerKey` claim SQL is identical to its `None` SQL
(it set the flag but never overrode the 5-arg method) is also rejected at startup — identical claim SQL
cannot enforce head-of-line ordering. Set `AllowDegradedOrdering = true` to downgrade the failure to a
startup warning and accept passthrough ordering.

> **Trust boundary.** These startup checks catch *accidental* misconfiguration (an undeclared capability,
> or a declared capability backed by passthrough SQL). They **cannot** verify that a custom dialect's
> `PerKey` SQL actually enforces head-of-line ordering — that is undecidable from outside the dialect. A
> dialect that declares `SupportsPerKeyHeadOfLineOrdering` and emits superficially different but
> semantically incorrect SQL will pass startup and may still deliver same-key messages out of order.
> **You are responsible for the correctness of your head-of-line predicate.** If you need a
> guaranteed-correct implementation, use a framework-provided dialect (PostgreSQL today).

```csharp
public sealed class SqlServerOutboxSqlDialect : IOutboxSqlDialect
{
    public string ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";

    // Declares that the 5-arg overload below emits a real head-of-line predicate.
    // Required for PerKey — without it, startup fails fast.
    public bool SupportsPerKeyHeadOfLineOrdering => true;

    // 4-arg: used by None mode — bit-identical to pre-PerKey behavior.
    public FormattableString GetClaimSql(
        string instanceId, DateTimeOffset now, DateTimeOffset staleCutoff, int batchSize)
        => /* ... */;

    // 5-arg: MUST be overridden with a head-of-line predicate for PerKey ordering.
    public FormattableString GetClaimSql(
        string instanceId, DateTimeOffset now, DateTimeOffset staleCutoff, int batchSize,
        BareWire.Abstractions.Outbox.OrderingMode orderingMode)
        => orderingMode == BareWire.Abstractions.Outbox.OrderingMode.PerKey
            ? /* head-of-line SQL */
            : GetClaimSql(instanceId, now, staleCutoff, batchSize);
}
```

### SECURITY WARNING — Head-of-line denial of service (SEC-1)

> **MUST read before enabling `PerKey` with untrusted key sources.**

When `OrderingKeyHeaderName` points to a header that an external message producer controls,
an attacker can send **one permanently undeliverable message** with a chosen key.
That message becomes the head of its key stream and **permanently stalls all ordered delivery
for that key**.
The blocked head is retried on every polling cycle at frequency `1 / PollingInterval`,
consuming CPU and database write load proportional to the number of blocked keys.

**Recommendation:** Do **not** enable `OrderingMode.PerKey` in production with keys from
untrusted sources until bounded-retry with park/dead-letter (`MaxDeliveryAttempts`, R7.8)
is available.
R7.8 will cap the retry count per head and park permanently stuck rows, bounding the blast
radius of a head-of-line attack to a configurable number of retries.

Until R7.8 lands, restrict `PerKey` to scenarios where the ordering-key header is set by
**trusted, internal code** only (e.g. your own domain event publisher, not a message gateway
that forwards headers from external clients).

## Custom SQL Dialect

By default, the PostgreSQL dialect (`PostgresOutboxSqlDialect`) uses `FOR UPDATE SKIP LOCKED`.
The store invokes a dialect's atomic claim **only when its `IOutboxSqlDialect.ProviderName` matches
the active EF Core provider**; a provider without a matching dialect uses a non-atomic client-side
fallback (safe for a single dispatcher instance / testing, not for multi-instance production).

To get an atomic claim on another provider, implement `IOutboxSqlDialect` — set `ProviderName` to
your provider's name and return provider-appropriate claim SQL — and register it **before** calling
`AddBareWireOutbox`:

```csharp
public sealed class SqlServerOutboxSqlDialect : IOutboxSqlDialect
{
    public string ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";

    public FormattableString GetClaimSql(
        string instanceId, DateTimeOffset now, DateTimeOffset staleCutoff, int batchSize)
        => /* UPDATE ... SET LockedAt/LockedBy WHERE Id IN (SELECT TOP(n) ... WITH (UPDLOCK, READPAST)) */;
}

services.AddSingleton<IOutboxSqlDialect, SqlServerOutboxSqlDialect>(); // wins over the default (TryAdd)
services.AddBareWireOutbox(options => options.UseSqlServer(connectionString));
```

## Startup Fail-Fast Guard (Atomic-Provider Requirement)

At host startup, `OutboxProviderAtomicityChecker` verifies that both the registered
`IOutboxSqlDialect` and `IInboxSqlDialect` have a `ProviderName` matching the active EF Core
provider. If either dialect does not match, the host throws `BareWireConfigurationException`
and refuses to start.

This prevents the silent non-atomic fallback from activating in multi-instance deployments,
where it would break claim/dedup invariants and cause duplicate message delivery.

### Single-instance / testing opt-out

For single-instance deployments or test environments using a provider without a matching atomic
dialect (e.g. SQLite), set `AllowNonAtomicProvider = true`:

```csharp
services.AddBareWireOutbox(
    options => options.UseSqlite("DataSource=:memory:"),
    outbox =>
    {
        outbox.AllowNonAtomicProvider = true; // single-instance / testing only — see warning below
        outbox.AutoCreateSchema = true;
    });
```

**Warning:** `AllowNonAtomicProvider = true` activates the non-atomic client-side fallback.
A startup Warning is logged (EventId 7601) identifying the active provider and both dialect names.
This option is safe only when a single dispatcher instance is running. Never use it in
multi-instance production deployments.

### SQL Server

SQL Server requires a user-supplied `IOutboxSqlDialect` and `IInboxSqlDialect` with
`ProviderName = "Microsoft.EntityFrameworkCore.SqlServer"`. Register them before calling
`AddBareWireOutbox` so that `TryAddSingleton` keeps your registration. Without a custom dialect,
the startup guard throws `BareWireConfigurationException` (default dialects target PostgreSQL).

## Supported Databases

- **PostgreSQL** — full atomic claim with `FOR UPDATE SKIP LOCKED` (recommended for production)
- **SQL Server** — supply custom `IOutboxSqlDialect` and `IInboxSqlDialect` (see above)
- **SQLite** — client-side claim; set `AllowNonAtomicProvider = true`; not suitable for multi-instance production use

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
