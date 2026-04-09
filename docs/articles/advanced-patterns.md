# Advanced Patterns

## Multi-Consumer Partitioning

When multiple consumer types share a single endpoint, you can ensure per-correlation ordering using the `PartitionerMiddleware`.

### Setup

```csharp
builder.Services.AddPartitionerMiddleware(cfg, partitionCount: 64);

rmq.ReceiveEndpoint("event-processing", e =>
{
    e.ConcurrentMessageLimit = 16;
    e.Consumer<OrderEventConsumer, OrderEvent>();
    e.Consumer<PaymentEventConsumer, PaymentEvent>();
    e.Consumer<ShipmentEventConsumer, ShipmentEvent>();
});
```

### How It Works

- Messages are hashed by `CorrelationId` into one of 64 partitions
- Messages within the same partition are processed sequentially
- Messages in different partitions are processed in parallel
- This guarantees ordering per correlation while maximizing throughput

### Verifying Order

The MultiConsumerPartitioning sample generates 1000 events across 10 CorrelationIds and logs processing order:

```
POST /events/generate        — publish 1000 events
GET  /events/processing-log  — verify per-correlation ordering
```

> See: `samples/BareWire.Samples.MultiConsumerPartitioning/`

## Raw Message Interoperability

When integrating with legacy systems that publish raw JSON without BareWire conventions, use the raw consumer pattern.

### Custom Header Mapping

Map external header names to BareWire conventions:

```csharp
rmq.ConfigureHeaderMapping(headers =>
{
    headers.MapCorrelationId("X-Correlation-Id");
    headers.MapMessageType("X-Message-Type");
    headers.MapHeader("SourceSystem", "X-Source-System");
});
```

### Raw Consumer

Handle untyped messages with manual deserialization:

```csharp
public sealed class RawEventConsumer : IRawConsumer
{
    public async Task ConsumeAsync(RawConsumeContext context)
    {
        var sourceSystem = context.Headers["SourceSystem"];

        if (context.TryDeserialize<ExternalEvent>(out var evt))
        {
            // process typed event
        }
    }
}
```

### Typed Consumer for Known Messages

For known external message types, register a standard typed consumer alongside the raw one:

```csharp
rmq.ReceiveEndpoint("raw-events", e =>
{
    e.RawConsumer<RawEventConsumer>();
});

rmq.ReceiveEndpoint("typed-events", e =>
{
    e.Consumer<TypedEventConsumer, ExternalEvent>();
});
```

### Simulating a Legacy Publisher

The RawMessageInterop sample includes a `LegacyPublisher` background service that uses the bare `RabbitMQ.Client` to publish raw JSON — simulating an external system:

```csharp
public sealed class LegacyPublisher : BackgroundService
{
    // Publishes raw JSON to "legacy.events" exchange
    // with custom headers: X-Correlation-Id, X-Message-Type, X-Source-System
}
```

> See: `samples/BareWire.Samples.RawMessageInterop/`

## Topic-Based Selective Routing

Use topic exchanges with routing key patterns for selective subscriptions:

```csharp
topology.DeclareExchange("events", ExchangeType.Topic, durable: true);

// Only order events
topology.BindExchangeToQueue("events", "order-queue", routingKey: "order.*");

// Only payment events
topology.BindExchangeToQueue("events", "payment-queue", routingKey: "payment.*");

// All events (monitoring/saga)
topology.BindExchangeToQueue("events", "all-events", routingKey: "#");
```

> See: `samples/BareWire.Samples.ObservabilityShowcase/`
