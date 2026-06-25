# Advanced Patterns

## Multi-Consumer Partitioning

When multiple consumer types share a single endpoint, you can ensure per-key ordering with per-endpoint consumer ordering: `OrderedByHeader(...)` (raw / cross-language) or `OrderedBy<TMessage>(selector)` (typed). Per-key ordering is OFF by default — opt in per endpoint.

> See: [Per-Key Consumer Ordering](per-key-ordering.md) for the full picture — strategies (`Auto` / `LocalPartitioned` / `TransportNative`), transport affinity (SAC / consistent-hash), fail-fast, poison handling, and the end-to-end story with the outbox.

> The DI-level `AddPartitionerMiddleware(...)` is deprecated and retained for one coexistence release. Migrate to per-endpoint `OrderedByHeader`/`OrderedBy`.

### Setup

The producer stamps the ordering key on the `ordering-key` transport header; the endpoint opts in with `OrderedBy(...)`. For a single-instance consumer, declare the `LocalPartitioned` strategy explicitly (the default `Auto` strategy is capability-driven and fails fast on RabbitMQ unless a transport affinity is declared for cross-instance ordering):

```csharp
// Consumer endpoint — opt in to per-key ordering (single-instance, in-process fixed lanes)
rmq.ReceiveEndpoint("event-processing", e =>
{
    e.ConcurrentMessageLimit = 16;
    e.OrderedBy(o => o
        .ByHeader("ordering-key")
        .Strategy(ConsumerOrderingStrategy.LocalPartitioned));
    e.Consumer<OrderEventConsumer, OrderEvent>();
    e.Consumer<PaymentEventConsumer, PaymentEvent>();
    e.Consumer<ShipmentEventConsumer, ShipmentEvent>();
});

// Producer — stamp the ordering key on the transport header
var headers = new Dictionary<string, string> { ["ordering-key"] = correlationId };
await bus.PublishAsync(new OrderEvent(/* ... */), headers, cancellationToken);
```

> The `OrderedByHeader("ordering-key")` one-liner sets only the key source and leaves the strategy at `Auto`. On RabbitMQ that requires a declared `TransportAffinity` (SAC / ConsistentHash) for cross-instance ordering; for single-instance ordering use the block form with `LocalPartitioned` as shown above.

### How It Works

- The inbound runner reads the `ordering-key` header **before deserialization** and hashes it to a fixed lane (fixed-lane hashing)
- Messages sharing the same key are processed sequentially within their lane
- Messages with different keys are processed in parallel across lanes (lane count = `ConcurrentMessageLimit`)
- This guarantees ordering per key while maximizing throughput

> Ordering keys should be reasonably high-cardinality. Many distinct keys spread across the lanes maximize parallelism; a few hot keys serialize most traffic onto a small number of lanes.

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
