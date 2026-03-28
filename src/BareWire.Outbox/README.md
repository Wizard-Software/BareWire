# BareWire.Outbox

Transactional outbox and inbox pattern for BareWire ensuring exactly-once message delivery.

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

Full documentation: [BareWire on GitHub](https://github.com/asawicki/BareWire)

## License

MIT
