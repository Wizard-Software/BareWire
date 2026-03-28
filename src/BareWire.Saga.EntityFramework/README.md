# BareWire.Saga.EntityFramework

Entity Framework Core provider for BareWire SAGA state persistence.

## Installation

```bash
dotnet add package BareWire.Saga.EntityFramework
```

## Usage

```csharp
builder.AddBareWire(wire =>
{
    wire.UseSaga(saga =>
    {
        saga.UseEntityFramework<AppDbContext>();
    });
});
```

Requires EF Core migrations for saga state tables. See the documentation for migration setup.

## Supported Databases

- SQL Server
- PostgreSQL
- SQLite

## Documentation

Full documentation: [BareWire on GitHub](https://github.com/asawicki/BareWire)

## License

MIT
