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

## Dead Letter Exchanges

For retry exhaustion handling, configure RabbitMQ native DLX:

```csharp
// Main queue with DLX routing
topology.DeclareExchange("payments", ExchangeType.Direct, durable: true);
topology.DeclareQueue("payments", durable: true, arguments: new Dictionary<string, object>
{
    ["x-dead-letter-exchange"] = "payments.dlx"
});
topology.BindExchangeToQueue("payments", "payments");

// Dead letter queue
topology.DeclareExchange("payments.dlx", ExchangeType.Fanout, durable: true);
topology.DeclareQueue("payments-dlq", durable: true);
topology.BindExchangeToQueue("payments.dlx", "payments-dlq");
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
