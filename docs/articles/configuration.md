# Configuration

## Bus Registration

BareWire uses a fluent configuration API registered through `IServiceCollection`:

```csharp
builder.Services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(rmq =>
    {
        rmq.ConfigureTopology(topology => { /* ... */ });
        rmq.ReceiveEndpoint("my-queue", e => { /* ... */ });
    });
});
```

## JSON Serializer

BareWire follows a raw-first approach — the default serializer produces raw JSON without an envelope. Register it with:

```csharp
builder.AddBareWireJsonSerializer();
```

This uses `System.Text.Json` internally with zero-copy `IBufferWriter<byte>` / `ReadOnlySequence<byte>` pipelines. No `byte[]` is allocated per-message in the hot path.

## RabbitMQ Transport

### Connection

The connection string is typically injected via Aspire or configuration:

```csharp
// Via Aspire (automatic)
builder.AddRabbitMQClient("rabbitmq");

// Via connection string
rmq.Host("amqp://guest:guest@localhost:5672/");
```

### Receive Endpoint Options

Each receive endpoint supports the following settings:

```csharp
rmq.ReceiveEndpoint("my-queue", e =>
{
    e.PrefetchCount = 16;              // broker-level prefetch
    e.ConcurrentMessageLimit = 8;      // in-flight concurrency
    e.RetryCount = 3;                  // retry attempts before DLQ
    e.RetryInterval = TimeSpan.FromSeconds(1);

    e.Consumer<MyConsumer, MyMessage>();
});
```

### Per-Key Consumer Ordering

A receive endpoint can preserve message order **within a key** while processing different keys in parallel. It is OFF by default — opt in per endpoint with one of:

```csharp
rmq.ReceiveEndpoint("ordered-processing", e =>
{
    // Header-based (raw / cross-language) — leaves strategy at Auto
    e.OrderedByHeader("ordering-key");

    // Or the configurator block for full control
    e.OrderedBy(o =>
    {
        o.ByHeader("ordering-key");
        o.TransportAffinity(TransportAffinity.SingleActiveConsumer);
        o.MaxDeliveryAttempts(2);
    });

    e.Consumer<MyConsumer, MyMessage>();
});
```

> **Correlation-id key caveat.** When no explicit key source is given, the ordering key falls back to the auto-stamped correlation-id. This only works when the correlation-id is **stable per aggregate/entity** and has **appropriate cardinality**: too few distinct values create a hot key that throttles parallelism; a value that changes per message gives no real affinity (each message is its own group); and the correlation-id is **not** stamped for plain `PublishAsync`/`SendAsync`, so under that traffic the message flows keyless (no ordering). Prefer an explicit `ByHeader`/`By` key source for predictable behavior.

> See: [Per-Key Consumer Ordering](per-key-ordering.md) for strategies, transport affinity, fail-fast, and the end-to-end story.

## Topology Configuration

Use `ConfigureTopology` to declare exchanges, queues, and bindings. Queue arguments can be configured using the fluent `IQueueConfigurator` API:

```csharp
rmq.ConfigureTopology(topology =>
{
    topology.DeclareExchange("orders", ExchangeType.Topic, durable: true);
    topology.DeclareQueue("orders", durable: true, autoDelete: false, configure: q =>
    {
        q.SetQueueType(QueueType.Quorum)
         .DeadLetterExchange("orders.dlx")
         .MessageTtl(TimeSpan.FromDays(7));
    });
    topology.BindExchangeToQueue("orders", "orders", routingKey: "#");
});
```

> See: [Topology](topology.md) for full details and all available `IQueueConfigurator` methods.

## Flow Control Options

BareWire provides both consume-side and publish-side flow control. Register options via DI:

```csharp
// Consume-side: credit-based flow control
builder.Services.AddSingleton(new FlowControlOptions
{
    MaxInFlightMessages = 50,
    MaxInFlightBytes = 1_048_576  // 1 MiB
});

// Publish-side: bounded outgoing channel
builder.Services.AddSingleton(new PublishFlowControlOptions
{
    MaxPendingPublishes = 500
});
```

> See: `samples/BareWire.Samples.BackpressureDemo/Program.cs`

## SAGA Persistence

Register SAGA state persistence with EF Core:

```csharp
builder.Services.AddBareWireSaga<OrderSagaState>(
    options => options.UseNpgsql(connectionString));

// Or with SQLite
builder.Services.AddBareWireSaga<OrderSagaState>(
    options => options.UseSqlite("Data Source=saga.db"));
```

> See: `samples/BareWire.Samples.SagaOrderFlow/Program.cs`

## Transactional Outbox

Configure the outbox with a database provider and polling settings:

```csharp
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options.UseNpgsql(connectionString),
    configureOutbox: outbox =>
    {
        outbox.PollingInterval = TimeSpan.FromSeconds(1);
        outbox.DispatchBatchSize = 100;
    });
```

> See: `samples/BareWire.Samples.TransactionalOutbox/Program.cs`

## Observability

Enable OpenTelemetry integration:

```csharp
builder.Services.AddBareWireObservability(cfg =>
{
    cfg.EnableOpenTelemetry = true;
});
```

> See: `samples/BareWire.Samples.ObservabilityShowcase/Program.cs`

## Service Defaults

For consistent observability and health check setup across multiple services, use the shared `AddServiceDefaults()` extension:

```csharp
builder.AddServiceDefaults();  // during DI setup
// ...
app.MapServiceDefaults();      // after app.Build()
```

This registers OpenTelemetry tracing/metrics, OTLP exporter, and health check endpoints:
- `/health` — combined liveness + readiness
- `/health/live` — liveness only
- `/health/ready` — full readiness including dependencies

> See: `samples/BareWire.Samples.ServiceDefaults/ServiceDefaultsExtensions.cs`
