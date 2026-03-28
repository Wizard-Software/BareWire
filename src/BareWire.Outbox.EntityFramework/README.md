# BareWire.Outbox.EntityFramework

Entity Framework Core provider for BareWire Outbox with SQL Server, PostgreSQL, and SQLite support.

## Installation

```bash
dotnet add package BareWire.Outbox.EntityFramework
```

## Usage

```csharp
builder.AddBareWire(wire =>
{
    wire.UseOutbox(outbox =>
    {
        outbox.UseEntityFramework<AppDbContext>();
    });
});
```

Requires EF Core migrations for outbox/inbox tables. See the documentation for migration setup.

## Supported Databases

- SQL Server
- PostgreSQL
- SQLite

## Documentation

Full documentation: [BareWire on GitHub](https://github.com/asawicki/BareWire)

## License

MIT
