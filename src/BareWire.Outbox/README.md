# BareWire.Outbox

Transactional outbox and inbox pattern for BareWire. Delivery is **at-least-once**; combined with inbox deduplication (whose `ProcessedAt` marker commits atomically with the consumer's business transaction) it yields **exactly-once processing** (effectively-once).

## Installation

```bash
dotnet add package BareWire.Outbox
```

## Usage

```csharp
builder.AddBareWire(wire =>
{
    wire.UseOutbox(outbox =>
    {
        outbox.UseEntityFramework<AppDbContext>();
        outbox.DeliveryInterval = TimeSpan.FromSeconds(5);
    });
});
```

## Features

- Transactional outbox — messages are stored in the same DB transaction as business data
- Inbox deduplication — prevents duplicate message processing
- Configurable delivery interval and batch size
- Pluggable storage providers (EF Core, etc.)

## Retry observability

Every outbox row a transport rejects is released for a deferred retry (backing off up to
`OutboxLockTimeout` between attempts) instead of being dropped or retried without limit. A row that
keeps failing — a "poison" row — is never abandoned: this is an accepted, deliberate limitation, not a
bug, and it is why this retry class is made observable rather than eliminated.

| Instrument | Type | Unit | Meaning |
|---|---|---|---|
| `barewire.outbox.rows.retried` | Counter | `{row}` | Number of outbox rows released for a deferred retry after a transport rejection. Ordering-barrier siblings (confirmed rows held back only by per-key ordering) are never counted — they are not retries. |
| `barewire.outbox.retry.oldest_due_age` | Observable gauge | `s` | Age of the oldest outbox retry that is already due but not yet re-claimed by any dispatcher instance — including claims abandoned by a crashed instance, not only deferred nacks. Reports `0` when no retry is currently due, and reports no measurement at all before its first sample. |

Both instruments are created on a meter named `BareWire` (the same convention the observability package
uses) only when an `IMeterFactory` is available through dependency injection; without one, the retry log
below still works on its own. Neither instrument carries tags — a row id is never exposed as a tag
value, to keep cardinality bounded.

Alongside the metrics, the dispatcher emits a **rate-limited Warning** log entry — at most one per
minute per dispatcher instance (so a cluster of N dispatchers can emit up to N per minute, one each) —
naming one representative row (the one with the highest retry count in the batch) by id and retry
count, plus how many rows in total were released for retry and how many were not individually logged
since the previous entry. The entry never includes the claim owner / instance identity, message body,
headers, routing key, or content type. A separate, unthrottled Debug-level entry still logs the full
per-batch count for detailed diagnostics.

The gauge is sampled lazily: the dispatcher queries the store for the oldest due retry only when a
metrics collector has actually observed the gauge since the previous sample — so a deployment that never
scrapes metrics pays no extra query, and the sampling frequency naturally tracks whatever frequency the
collector polls at. Because of this, the reported sample can lag by up to roughly one collection
interval behind the true current state.

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
