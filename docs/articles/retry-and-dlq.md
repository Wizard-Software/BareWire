# Retry and Dead Letter Queues

## Retry Policies

Configure retry attempts per receive endpoint:

```csharp
rmq.ReceiveEndpoint("payments", e =>
{
    e.RetryCount = 3;
    e.RetryInterval = TimeSpan.FromSeconds(1);
    e.Consumer<PaymentProcessor, ProcessPayment>();
});
```

When a consumer throws an exception, BareWire retries the message up to `RetryCount` times with `RetryInterval` delay between attempts. After all retries are exhausted, the message is either nacked or routed to a Dead Letter Exchange.

## Dead Letter Exchange (DLX)

RabbitMQ's native DLX mechanism routes failed messages to a separate queue for inspection and reprocessing.

### Topology Setup

```csharp
rmq.ConfigureTopology(topology =>
{
    // Main exchange and queue with DLX routing
    topology.DeclareExchange("payments", ExchangeType.Direct, durable: true);
    topology.DeclareQueue("payments", durable: true, arguments: new Dictionary<string, object>
    {
        ["x-dead-letter-exchange"] = "payments.dlx"
    });
    topology.BindExchangeToQueue("payments", "payments");

    // Dead letter exchange and queue
    topology.DeclareExchange("payments.dlx", ExchangeType.Fanout, durable: true);
    topology.DeclareQueue("payments-dlq", durable: true);
    topology.BindExchangeToQueue("payments.dlx", "payments-dlq");
});
```

### DLQ Consumer

Create a consumer on the dead letter queue to handle failed messages — log them, persist them, or trigger alerts:

```csharp
public sealed class DlqConsumer : IConsumer<ProcessPayment>
{
    private readonly PaymentDbContext _db;

    public DlqConsumer(PaymentDbContext db) => _db = db;

    public async Task ConsumeAsync(ConsumeContext<ProcessPayment> context)
    {
        _db.FailedPayments.Add(new FailedPayment
        {
            OrderId = context.Message.OrderId,
            Amount = context.Message.Amount,
            FailedAt = DateTime.UtcNow,
            Reason = "Retry limit exceeded"
        });

        await _db.SaveChangesAsync(context.CancellationToken);
    }
}
```

Register the DLQ consumer on its own endpoint:

```csharp
rmq.ReceiveEndpoint("payments-dlq", e =>
{
    e.Consumer<DlqConsumer, ProcessPayment>();
});
```

> See: `samples/BareWire.Samples.RetryAndDlq/`

## Message Loss Protection

If a queue has no Dead Letter Exchange configured and a message is rejected after retry exhaustion, it is **permanently lost**. BareWire logs a warning in this situation:

```
warn: BareWire.Pipeline.DeadLetterMiddleware
      Message {MessageId} on endpoint {Endpoint} was NACKed but no DLX is configured — message will be permanently lost.
```

The framework detects missing DLX via `EndpointBinding.HasDeadLetterExchange`, which is set based on the declared topology. Recommendation: **always configure a DLX** on production queues to avoid silent message loss.

## Inspecting Failed Messages

The RetryAndDlq sample exposes an endpoint to query persisted failures:

```
GET /payments/failed   — returns all failed payments from the DLQ consumer
```
