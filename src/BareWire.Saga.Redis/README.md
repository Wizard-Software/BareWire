# BareWire.Saga.Redis

Redis provider for BareWire SAGA state persistence using StackExchange.Redis.

## Installation

```bash
dotnet add package BareWire.Saga.Redis
```

## Usage

```csharp
// 1. Register the Redis connection (TLS, mutual TLS, Sentinel, and Cluster supported).
builder.Services.AddBareWireRedisConnection(opts =>
{
    opts.Endpoints.Add("localhost:6379");
});

// 2. Register Redis-backed persistence for each saga state type.
builder.Services.AddBareWireSagaRedis<OrderSagaState>(options =>
{
    options.StateTtl = TimeSpan.FromHours(24);
});
```

`AddBareWireSagaRedis<TSaga>` registers `ISagaRepository<TSaga>` with a scoped lifetime and uses
Lua-script optimistic concurrency for atomic state updates. Access is identity-only (lookup by
`CorrelationId`); arbitrary queries are not supported.

## Connection Support

- TLS and mutual TLS (client certificate via PFX)
- Redis Sentinel (high availability)
- Redis Cluster (sharding)

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
