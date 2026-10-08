# BareWire.Saga.EntityFramework

Entity Framework Core provider for BareWire SAGA state persistence.

## Installation

```bash
dotnet add package BareWire.Saga.EntityFramework
```

## Usage

```csharp
builder.Services.AddBareWireSaga<OrderSagaState>(
    options => options.UseNpgsql(connectionString));
```

Requires EF Core migrations for saga state tables. See the documentation for migration setup.

## Supported Databases

- SQL Server
- PostgreSQL
- SQLite

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
