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
