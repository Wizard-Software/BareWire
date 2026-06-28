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

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
