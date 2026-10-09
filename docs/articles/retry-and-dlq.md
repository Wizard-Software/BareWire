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

### Per-consumer retry

A consumer can carry its own retry policy, either inline or in a [consumer definition](consumer-definitions.md):

```csharp
rmq.ReceiveEndpoint("payments", e =>
{
    e.RetryCount = 3;
    e.RetryInterval = TimeSpan.FromSeconds(1);
    e.Consumer<PaymentProcessor, ProcessPayment>(c =>
        c.Retry(r => r.Exponential(4, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2))));
});
```

A policy set with `consumer.Retry(...)` replaces the endpoint `RetryCount`/`RetryInterval` for that consumer; the two are not combined, so the message is not retried by both. Consumers without their own policy, raw consumers and sagas keep the endpoint retry.

### Limits

Retry configuration is validated when the bus starts. An out-of-range value makes `StartAsync` throw a `BareWireConfigurationException` that names the offending option, so a misconfiguration never surfaces later while a message is being handled:

| Setting | Allowed range |
|---|---|
| Retry count (`RetryCount`, or the `retryCount` argument of `Interval`/`Incremental`/`Exponential`) | 0 to 100 |
| Every single delay (`RetryInterval`, `interval`, `initial`, `maxInterval`) | 0 to 1 hour |
| `Incremental` largest delay (`initial + increment * (retryCount - 1)`) | at most 1 hour |

The endpoint `RetryInterval` is only checked when `RetryCount` is greater than zero. At runtime every computed delay is additionally clamped to one hour.

### Side effects of in-process retry

Retries run inside the process, while the message is still being handled:

- The delivery and its flow-control credit are held for the whole backoff.
- When per-key ordering is disabled, the endpoint pump is blocked until the message succeeds or the retries are exhausted. For example, `Exponential(4, 200 ms, 2 s)` can hold the pump for about 3 seconds per poison message.
- The consumer concurrency slot and the sequential read loop (or the per-key ordered lane) are held as well, so one poisoned message can stall the endpoint for the whole backoff. With the maximum configuration (100 retries of 1 hour each) that is about 100 hours, and the bus health check can report `Degraded` meanwhile.
- The broker may give up before the backoff ends: RabbitMQ closes the channel after `consumer_timeout` (30 minutes by default), Azure Service Bus message locks expire, SQS makes the message visible again after the visibility timeout, and Kafka triggers a rebalance after `max.poll.interval.ms`. The limits above do not account for these timeouts.
- Keep consumer backoffs short, or rely on the dead letter queue and redelivery for longer delays.

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
    topology.BindExchangeToQueue("payments", "payments", routingKey: "");

    // Dead letter exchange and queue
    topology.DeclareExchange("payments.dlx", ExchangeType.Fanout, durable: true);
    topology.DeclareQueue("payments-dlq", durable: true);
    topology.BindExchangeToQueue("payments.dlx", "payments-dlq", routingKey: "");
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

## Per-Key Poison Handling

When an endpoint enables [per-key consumer ordering](per-key-ordering.md), a poison message at the head of a key would otherwise block that key's entire stream (head-of-line blocking). The anti-starvation contract resolves this: **bounded retry → park/DLQ → key release**.

```csharp
rmq.ReceiveEndpoint("ordered-processing", e =>
{
    e.OrderedBy(o =>
    {
        o.ByHeader("ordering-key");
        o.TransportAffinity(TransportAffinity.SingleActiveConsumer);
        o.MaxDeliveryAttempts(2);   // park the poison head after 2 attempts
    });
    e.Consumer<OrderShippedConsumer, OrderShipped>();
});
```

- The head message is retried up to `MaxDeliveryAttempts` (by the consumer's own retry policy if it has one, otherwise by the endpoint `RetryCount`/`RetryInterval`).
- After the threshold, the message is dead-lettered (wire a DLX on the queue) and leaves the head of the key.
- The key stream then **resumes** for subsequent messages. The skipped (parked) message is an ordering gap, which is logged — there is no permanent block.
- Key release happens only **after the broker durably confirms** the parking of the head; if settlement fails, the key is not released (the head stays at the front and the operation is retried).

`MaxDeliveryAttempts` defaults to `0` (disabled — plain at-least-once without parking).

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
