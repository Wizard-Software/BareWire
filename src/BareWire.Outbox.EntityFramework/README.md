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

## Supported Databases

- **PostgreSQL** — full atomic claim with `FOR UPDATE SKIP LOCKED` (recommended for production)
- **SQL Server** — supply a custom `IOutboxSqlDialect` (`ProviderName` = `"Microsoft.EntityFrameworkCore.SqlServer"`) using `WITH (UPDLOCK, READPAST)`; the store invokes it once the provider name matches
- **SQLite** — client-side claim for testing; not suitable for multi-instance production use

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
