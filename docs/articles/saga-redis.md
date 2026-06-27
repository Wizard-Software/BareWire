# Redis Saga Persistence

`BareWire.Saga.Redis` is a Redis-backed persistence provider for BareWire SAGA state, built on StackExchange.Redis. It stores each saga instance as a Redis Hash and enforces optimistic concurrency server-side with Lua scripts, so check-and-update happens atomically in a single round trip without client-side locking.

Access is identity-only: saga instances are looked up exclusively by `CorrelationId`. Arbitrary predicate queries are not supported, so this provider does not register `IQueryableSagaRepository`. For the state-machine model, message handling, and the `ISagaState`/`ISagaRepository<TState>` contract this provider implements, see the [Saga State Machines](saga.md) guide — this article is its persistence-backend companion.

## Registration

Registration is two steps: configure the Redis connection, then register Redis-backed persistence for each saga state type.

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

`AddBareWireSagaRedis<TSaga>` registers `ISagaRepository<TSaga>` with a **scoped** lifetime, where `TSaga : class, ISagaState`. It does **not** register the connection itself — that is the job of `AddBareWireRedisConnection`, which registers a StackExchange.Redis `IConnectionMultiplexer` as a singleton.

`AddBareWireRedisConnection` uses `TryAddSingleton` semantics: calling it twice does not add a second registration, and if your application already registered an `IConnectionMultiplexer`, the existing one is left in place. Configuration is validated **eagerly** at call time (not at first resolve), so misconfiguration — no endpoints, or TLS required but disabled — throws `BareWireConfigurationException` at startup. The connection is established synchronously inside the DI factory; because `AbortOnConnectFail` defaults to `false`, the factory returns quickly even when Redis is temporarily unavailable and StackExchange.Redis reconnects in the background.

## Repository Options

`RedisSagaRepositoryOptions` controls how saga state is keyed and expired:

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `KeyPrefix` | `string` | the saga type name (e.g. `OrderSagaState`) | Namespaces saga entries. The full key format is `{KeyPrefix}:{CorrelationId:D}`. |
| `StateTtl` | `TimeSpan?` | `null` (no expiry) | Optional time-to-live applied to each saga entry. |

`KeyPrefix` is a trusted, developer-controlled value — do not populate it from end-user input, and avoid characters with special meaning in Redis keys such as `:`, `{`, `}`, or whitespace, which can interfere with cluster hash tags or key scanning.

When `StateTtl` is `null` (the default), entries persist indefinitely until explicitly deleted. Setting a non-null TTL risks Redis evicting a live saga's state before it reaches a terminal state, which makes `FindAsync` return `null` for a saga that is logically still active. Use a TTL only when saga lifetimes are well-bounded and shorter than the configured value.

> Security note: saga state is stored as unencrypted plaintext JSON in Redis. At-rest confidentiality is the responsibility of the connection/deployment configuration (use TLS in transit and Redis-level encryption). Do not store secrets or PII in saga state unless the Redis deployment is appropriately secured.

## Connection and Authentication Options

`RedisConnectionOptions` (passed to `AddBareWireRedisConnection`) supports single-node, Sentinel, and Cluster topologies:

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `Endpoints` | `IList<string>` | empty (at least one required) | Redis endpoints in `host` or `host:port` format. Multiple endpoints activate Cluster or Sentinel mode. |
| `Password` | `string?` | `null` | Password for Redis authentication. |
| `User` | `string?` | `null` | ACL username for Redis 6+ authentication. |
| `Ssl` | `bool` | `false` | Enables TLS for the connection. |
| `SslHost` | `string?` | `null` | Expected server name for TLS certificate validation. |
| `ClientCertificatePfxPath` | `string?` | `null` | Path to the PFX certificate for mutual TLS. |
| `ClientCertificatePfxPassword` | `string?` | `null` | Password for the PFX file (`null` = no password). |
| `RequireTlsInProduction` | `bool` | `true` | When `true` and `Ssl` is `false`, configuration build throws. |
| `ServiceName` | `string?` | `null` | Sentinel service name; non-empty activates Sentinel mode. |
| `AbortOnConnectFail` | `bool` | `false` | Whether to abort immediately if the initial connection fails. |
| `ConnectRetry` | `int` | `3` | Number of connection retry attempts. |
| `ConnectTimeout` | `int?` | `null` (StackExchange.Redis default) | Connection timeout in milliseconds. |
| `ClientName` | `string?` | `null` | Connection label visible via the Redis `CLIENT LIST` command. |

### TLS

Enable transport encryption by setting `Ssl`. `RequireTlsInProduction` defaults to `true`, so a configuration with TLS disabled throws `BareWireConfigurationException` — set `RequireTlsInProduction = false` only for development or test environments.

```csharp
builder.Services.AddBareWireRedisConnection(opts =>
{
    opts.Endpoints.Add("redis.internal:6380");
    opts.Ssl = true;
    opts.SslHost = "redis.internal";
    opts.Password = "<password>";
});
```

### Mutual TLS

Provide a client certificate via PFX. Setting `ClientCertificatePfxPath` calls `ConfigurationOptions.SetUserPfxCertificate`, which also implicitly enables TLS. The file must exist at build time, otherwise configuration build throws.

```csharp
builder.Services.AddBareWireRedisConnection(opts =>
{
    opts.Endpoints.Add("redis.internal:6380");
    opts.Ssl = true;
    opts.ClientCertificatePfxPath = "/etc/secrets/redis-client.pfx";
    opts.ClientCertificatePfxPassword = "<pfx-password>";
});
```

### Sentinel

Set `ServiceName` to operate in Sentinel mode for high availability. The configured `Endpoints` are then treated as Sentinel node addresses.

```csharp
builder.Services.AddBareWireRedisConnection(opts =>
{
    opts.Endpoints.Add("sentinel-1:26379");
    opts.Endpoints.Add("sentinel-2:26379");
    opts.ServiceName = "mymaster";
});
```

### Cluster

Provide multiple endpoints to activate Cluster mode for sharding.

```csharp
builder.Services.AddBareWireRedisConnection(opts =>
{
    opts.Endpoints.Add("redis-node-1:6379");
    opts.Endpoints.Add("redis-node-2:6379");
    opts.Endpoints.Add("redis-node-3:6379");
});
```

## Optimistic Concurrency

Saga state is stored as a Redis Hash with two fields: `state` (UTF-8 JSON bytes) and `version` (an integer). The `Version` property on your saga state (see the [Saga State Machines](saga.md) guide) drives concurrency control, and the repository enforces it server-side via Lua scripts so the check-and-update is atomic.

- `SaveAsync` inserts a new saga only if no entry already exists for its `CorrelationId`. If one does, it throws `InvalidOperationException`.
- `UpdateAsync` increments `Version` and applies the change only when the stored version matches the expected version. On a version mismatch — or when the saga is missing — it throws `ConcurrencyException` and restores the in-memory `Version` so the caller's object is left consistent.
- `FindAsync` returns `null` when no entry exists for the given `CorrelationId`.
- `DeleteAsync` removes the entry for a `CorrelationId`.

## See also

- [API Reference](../api/index.md)
- [Saga State Machines](saga.md)
- [Outbox](outbox.md)
