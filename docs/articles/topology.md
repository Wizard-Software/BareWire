# Topology

BareWire uses manual topology by default — you explicitly declare exchanges, queues, and bindings. There is no auto-topology magic. This gives you full control over your RabbitMQ infrastructure.

## Exchange Types

### Fanout

All messages go to all bound queues. Use for broadcast/pub-sub:

```csharp
topology.DeclareExchange("messages", ExchangeType.Fanout, durable: true);
topology.DeclareQueue("messages", durable: true);
topology.BindExchangeToQueue("messages", "messages");
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

Publish with a specific routing key:

```csharp
await bus.PublishAsync(new DemoOrderCreated(...), routingKey: "order.created", ct);
```

> See: `samples/BareWire.Samples.ObservabilityShowcase/Program.cs`

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
topology.BindExchangeToQueue("payments", "payments");

// Dead letter queue
topology.DeclareExchange("payments.dlx", ExchangeType.Fanout, durable: true);
topology.DeclareQueue("payments-dlq", durable: true);
topology.BindExchangeToQueue("payments.dlx", "payments-dlq");
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
