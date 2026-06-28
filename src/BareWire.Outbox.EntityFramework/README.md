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

### Polling Cost

Each polling cycle issues one claim `UPDATE` per instance (affecting `0..DispatchBatchSize` rows),
even when no rows are pending. At the default `PollingInterval` of `1s` that is roughly one write
statement per second per instance. For low-throughput deployments — or when running many instances —
raise `PollingInterval` to reduce steady-state write/WAL load and bound write-amplification on the
shared `OutboxMessages` table.

### Nacked Rows (Partial Send Failures)

Rows that fail to publish (nacked by the broker) are NOT explicitly unlocked.
They become eligible for re-delivery after `OutboxLockTimeout` elapses.
To minimize retry latency, set `OutboxLockTimeout` conservatively relative to
your broker's worst-case publish-confirm time.

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

If you implement a custom `IOutboxSqlDialect` (e.g. for SQL Server), you must **override the
5-argument `GetClaimSql` overload** to emit a head-of-line predicate for `PerKey`.
The default interface implementation delegates to the 4-argument overload (passthrough —
no ordering), and the outbox logs a startup warning when `PerKey` is active but the dialect
appears to ignore it (the claim SQL for `PerKey` and `None` are identical).

```csharp
public sealed class SqlServerOutboxSqlDialect : IOutboxSqlDialect
{
    public string ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";

    // 4-arg: used by None mode — bit-identical to pre-PerKey behavior.
    public FormattableString GetClaimSql(
        string instanceId, DateTimeOffset now, DateTimeOffset staleCutoff, int batchSize)
        => /* ... */;

    // 5-arg: MUST be overridden for PerKey head-of-line ordering.
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
