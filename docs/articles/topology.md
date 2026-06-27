# Topology

BareWire uses manual topology by default — you explicitly declare exchanges, queues, and bindings. There is no auto-topology magic. This gives you full control over your RabbitMQ infrastructure.

## Exchange Types

### Fanout

All messages go to all bound queues. Use for broadcast/pub-sub:

```csharp
topology.DeclareExchange("messages", ExchangeType.Fanout, durable: true);
topology.DeclareQueue("messages", durable: true);
topology.BindExchangeToQueue("messages", "messages", routingKey: ""); // fanout ignores the key
```

> See: `samples/BareWire.Samples.BasicPublishConsume/Program.cs`

### Direct

Messages routed by exact routing key match. Use for point-to-point commands:

```csharp
topology.DeclareExchange("order-validation", ExchangeType.Direct, durable: true);
topology.DeclareQueue("order-validation", durable: true);
topology.BindExchangeToQueue("order-validation", "order-validation", routingKey: "");
```

> See: `samples/BareWire.Samples.RequestResponse/Program.cs`

### Topic

Messages routed by pattern-matching routing keys. Use for selective subscriptions:

```csharp
topology.DeclareExchange("events", ExchangeType.Topic, durable: true);

// Order events
topology.DeclareQueue("demo-orders", durable: true);
topology.BindExchangeToQueue("events", "demo-orders", routingKey: "order.*");

// Payment events
topology.DeclareQueue("demo-payments", durable: true);
topology.BindExchangeToQueue("events", "demo-payments", routingKey: "payment.*");

// Saga receives everything
topology.DeclareQueue("demo-saga", durable: true);
topology.BindExchangeToQueue("events", "demo-saga", routingKey: "#");
```

Map each message type to a routing key once at configuration time, then publish normally —
`PublishAsync<T>` applies the mapped key automatically (without a mapping it falls back to
`typeof(T).FullName`):

```csharp
// At configuration time, on the RabbitMQ configurator:
rmq.MapRoutingKey<DemoOrderCreated>("order.created");

// At the call site:
await bus.PublishAsync(new DemoOrderCreated(...), ct);
```

To group an exchange and routing key for a type in one block, see the `Publish<T>(...)` and
`DeclareExchange<T>(...)` shapes in [Publishing and Consuming](publishing-and-consuming.md#ergonomic-per-type-send-mapping).

> See: `samples/BareWire.Samples.ObservabilityShowcase/Program.cs`

### Consistent Hash

Routes each message to one of several bound queues by hashing a key, so the same key always lands on the same queue. Use for per-key consumer ordering with parallelism across keys (one active consumer per queue):

```csharp
topology.DeclareExchange("ordered-events", ExchangeType.ConsistentHash, durable: true);
```

`ExchangeType.ConsistentHash` requires the broker plugin `rabbitmq_consistent_hash_exchange` to be enabled. It is an opt-in alternative to single-active-consumer; note that re-mapping (adding/removing a bound queue or a node restart) re-hashes keys and briefly breaks per-key order, which is why single-active-consumer is the recommended default.

> See: [Per-Key Consumer Ordering](per-key-ordering.md) for the two transport-affinity paths and their trade-offs.

## Bindings

`BindExchangeToQueue(exchange, queue, routingKey)` routes matching messages from an exchange to a
queue — the binding used in every example above. The `routingKey` is required: pass `""` for
fanout exchanges (which ignore it), an exact key for direct exchanges, or a pattern (`order.*`,
`#`) for topic exchanges.

For hierarchical or fan-out routing topologies you can also bind an exchange to another exchange
with `BindExchangeToExchange(source, destination, routingKey)`. Messages published to `source`
that match `routingKey` are forwarded to `destination`, which then applies its own bindings — for
example, a single top-level topic exchange feeding several domain exchanges:

```csharp
topology.DeclareExchange("all-events", ExchangeType.Topic, durable: true);
topology.DeclareExchange("order-events", ExchangeType.Topic, durable: true);

// Forward every "order.*" message from the top-level exchange to the order-events exchange.
topology.BindExchangeToExchange("all-events", "order-events", routingKey: "order.#");
```

## Queue Configuration (IQueueConfigurator)

`IQueueConfigurator` provides a typed, fluent API for common RabbitMQ queue arguments. It eliminates error-prone string keys like `"x-dead-letter-exchange"` or `"x-message-ttl"`.

Use the `DeclareQueue` overload that accepts `Action<IQueueConfigurator>`:

```csharp
// Fluent API (recommended)
topology.DeclareQueue("order-processing", durable: true, autoDelete: false, configure: q =>
{
    q.DeadLetterExchange("orders.events.dlx")
     .DeadLetterRoutingKey("orders.dead")
     .MessageTtl(TimeSpan.FromHours(24))
     .SetQueueType(QueueType.Quorum);
});

// MaxLength with overflow strategy
topology.DeclareQueue("bounded-queue", durable: true, autoDelete: false, configure: q =>
{
    q.MaxLength(10_000)
     .Overflow(OverflowStrategy.RejectPublish)
     .DeadLetterExchange("bounded.dlx");
});

// Escape hatch for uncommon arguments
topology.DeclareQueue("priority-queue", durable: true, autoDelete: false, configure: q =>
{
    q.SetQueueType(QueueType.Classic)
     .Argument("x-max-priority", 10);
});
```

The raw dictionary overload remains available as an alternative:

```csharp
topology.DeclareQueue("payments", durable: true, arguments: new Dictionary<string, object>
{
    ["x-dead-letter-exchange"] = "payments.dlx"
});
```

### Available Methods

| Method | RabbitMQ Argument | Description |
|--------|-------------------|-------------|
| `DeadLetterExchange(string)` | `x-dead-letter-exchange` | Routes rejected/expired messages to a DLX |
| `DeadLetterRoutingKey(string)` | `x-dead-letter-routing-key` | Overrides routing key for dead-lettered messages |
| `MessageTtl(TimeSpan)` | `x-message-ttl` | Per-queue message time-to-live (auto-converted to ms) |
| `MaxLength(long)` | `x-max-length` | Maximum message count in the queue |
| `MaxLengthBytes(long)` | `x-max-length-bytes` | Maximum total bytes in the queue |
| `SetQueueType(QueueType)` | `x-queue-type` | Classic, Quorum, or Stream |
| `Overflow(OverflowStrategy)` | `x-overflow` | What happens when max length is reached |
| `SingleActiveConsumer(bool)` | `x-single-active-consumer` | Promotes exactly one active consumer per queue — the transport-native affinity for per-key consumer ordering |
| `Argument(string, object)` | Any | Escape hatch for any queue argument |

### RabbitMQ Defaults Reference

| Argument | Default (when not set) |
|----------|----------------------|
| `x-queue-type` | `classic` |
| `x-overflow` | `drop-head` |
| `x-message-ttl` | No expiry |
| `x-max-length` | Unlimited |
| `x-dead-letter-exchange` | Messages discarded on reject/expire |

> **Warning:** Without a dead-letter exchange configured, rejected or expired messages are permanently discarded. For production queues, always configure a DLX to avoid silent message loss.

## Dead Letter Exchanges

For retry exhaustion handling, configure RabbitMQ native DLX. The recommended approach uses `IQueueConfigurator`:

```csharp
// Main queue with DLX routing (fluent API)
topology.DeclareExchange("payments", ExchangeType.Direct, durable: true);
topology.DeclareQueue("payments", durable: true, autoDelete: false, configure: q =>
{
    q.DeadLetterExchange("payments.dlx")
     .SetQueueType(QueueType.Quorum);
});
topology.BindExchangeToQueue("payments", "payments", routingKey: "");

// Dead letter queue
topology.DeclareExchange("payments.dlx", ExchangeType.Fanout, durable: true);
topology.DeclareQueue("payments-dlq", durable: true);
topology.BindExchangeToQueue("payments.dlx", "payments-dlq", routingKey: "");
```

### Typical Production Configuration

```csharp
topology.DeclareQueue("orders", durable: true, autoDelete: false, configure: q =>
{
    q.SetQueueType(QueueType.Quorum)           // HA replication
     .DeadLetterExchange("orders.dlx")          // capture failed messages
     .MessageTtl(TimeSpan.FromDays(7))          // auto-expire after 7 days
     .MaxLength(1_000_000)                       // bound queue size
     .Overflow(OverflowStrategy.RejectPublish); // backpressure to publishers
});
```

> See: `samples/BareWire.Samples.RetryAndDlq/Program.cs`

## Multi-Consumer Endpoints

A single queue can host multiple consumer types. BareWire routes by deserialized CLR type:

```csharp
topology.DeclareExchange("events", ExchangeType.Topic, durable: true);
topology.DeclareQueue("event-processing", durable: true);
topology.BindExchangeToQueue("events", "event-processing", routingKey: "#");

rmq.ReceiveEndpoint("event-processing", e =>
{
    e.Consumer<OrderEventConsumer, OrderEvent>();
    e.Consumer<PaymentEventConsumer, PaymentEvent>();
    e.Consumer<ShipmentEventConsumer, ShipmentEvent>();
});
```

> See: `samples/BareWire.Samples.MultiConsumerPartitioning/Program.cs`
