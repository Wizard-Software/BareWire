# Flow Control and Backpressure

BareWire provides deterministic memory usage through credit-based flow control on both the consume and publish sides.

## Consume-Side Flow Control

Limits the number of messages being processed concurrently. When limits are reached, BareWire stops fetching from the broker until capacity is available.

```csharp
builder.Services.AddSingleton(new FlowControlOptions
{
    MaxInFlightMessages = 50,
    MaxInFlightBytes = 1_048_576  // 1 MiB
});
```

- **MaxInFlightMessages** — maximum concurrent messages across all consumers
- **MaxInFlightBytes** — memory cap for in-flight message payloads

### Endpoint-Level Limits

Each receive endpoint also supports its own concurrency settings:

```csharp
rmq.ReceiveEndpoint("my-queue", e =>
{
    e.PrefetchCount = 16;           // broker-level prefetch
    e.ConcurrentMessageLimit = 8;   // BareWire in-flight limit
    e.Consumer<MyConsumer, MyMessage>();
});
```

## Publish-Side Backpressure

Limits the number of messages waiting to be sent. When the publish buffer is full, `PublishAsync` applies backpressure to the caller:

```csharp
builder.Services.AddSingleton(new PublishFlowControlOptions
{
    MaxPendingPublishes = 500
});
```

## Health Alerts

BareWire reports health status based on flow control utilization:

| Utilization | Health Status |
|---|---|
| < 90% | Healthy |
| >= 90% | Degraded |
| Buffer full | Unhealthy |

Health checks are available via the standard ASP.NET Core health check pipeline at `/health/ready`.

## Load Testing

The BackpressureDemo sample includes a load generator to test flow control behavior:

```
POST /load-test/start?rate=1000   — start publishing at 1000 msgs/s
POST /load-test/stop              — stop the load generator
GET  /metrics                     — real-time throughput and backpressure status
```

The `SlowConsumer` in the demo processes messages with a 100ms delay, so backpressure naturally slows the publisher to match consumer throughput.

> See: `samples/BareWire.Samples.BackpressureDemo/`
