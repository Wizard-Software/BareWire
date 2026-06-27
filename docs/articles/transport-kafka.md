# Kafka Transport

The Kafka transport (`BareWire.Transport.Kafka`) lets BareWire publish and consume over Apache
Kafka. It keeps BareWire's core principles — manual topology, raw-first payloads, bounded flow
control — and maps them onto Kafka concepts: an idempotent producer, consumer groups with
partition assignment, and an opt-in retry-topic + DLQ-topic pattern (Kafka has no native dead
letter queue).

It is built on top of `Confluent.Kafka`. The transport defaults to `SecurityProtocol=Plaintext`
(SASL/SSL is not yet wired up), so do not point it at a production broker until the secure-config
layer is in place.

## Registration

As with every BareWire transport, you register the **core engine** and the **Kafka transport**
together. There are two ways to do it (see [Configuration](configuration.md) for the full
rationale).

### 1. Single call — bundle package (recommended)

The `BareWire.Kafka` bundle depends on both the core and the transport and exposes one method,
`AddBareWireWithKafka`:

```csharp
using BareWire.Transport.Kafka;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;

builder.Services.AddBareWireWithKafka(
    transport =>
    {
        transport.BootstrapServers("localhost:9092");
        transport.ConsumerGroup("order-processing");
        transport.ConsumerAutoOffsetReset(AutoOffsetReset.Earliest);
        transport.ConsumerPartitionAssignmentStrategy(KafkaPartitionAssignmentStrategy.CooperativeSticky);
    },
    bus =>
    {
        bus.AddConsumer<OrderConsumer>();
        // serializers, middleware, endpoints...
    });
```

The `bus` delegate is optional — omit it when transport defaults are enough:

```csharp
builder.Services.AddBareWireWithKafka(transport => transport.BootstrapServers("localhost:9092"));
```

### 2. Two calls — core and transport registered separately

`AddBareWireWithKafka` is sugar over the explicit pair. Use the two-call form when you reference
the core and transport packages separately, or when an application needs more than one transport:

```csharp
builder.Services.AddBareWireKafka(kafka => kafka.BootstrapServers("localhost:9092"));
builder.Services.AddBareWire(bus => bus.AddConsumer<OrderConsumer>());
```

`AddBareWireKafka` registers the Kafka `ITransportAdapter` and `AddBareWire` the core engine. At
minimum, `BootstrapServers` must be called.

## Producer

The produce path runs with an **idempotent producer** by default, giving exactly-once delivery
semantics within a single producer session. Ordering is preserved by mapping each message key to a
partition, so messages that share a key keep their relative order. No producer-side tuning is
required beyond `BootstrapServers`.

## Consumer groups

Consuming uses Kafka consumer groups. All consumers that share a `ConsumerGroup` id coordinate
partition assignment and offset commits through the Kafka group coordinator. A group id is
**required** to consume.

```csharp
builder.Services.AddBareWireKafka(kafka =>
{
    kafka.BootstrapServers("localhost:9092");
    kafka.ConsumerGroup("order-processing");
    kafka.ConsumerAutoOffsetReset(AutoOffsetReset.Earliest);
    kafka.ConsumerPartitionAssignmentStrategy(KafkaPartitionAssignmentStrategy.CooperativeSticky);
});
```

- `ConsumerAutoOffsetReset` controls where a group starts when it has no committed offset (or the
  committed offset is out of range). Defaults to `AutoOffsetReset.Earliest`.
- `ConsumerPartitionAssignmentStrategy` selects the rebalancing strategy. Defaults to
  `KafkaPartitionAssignmentStrategy.CooperativeSticky`, which rebalances incrementally and
  minimises stop-the-world pauses. The other values are `Range` and `RoundRobin`.

Offsets are committed only after a message is settled successfully, giving at-least-once delivery.

## Retry-topic and DLQ-topic pattern

Kafka has no native dead letter queue, so BareWire emulates one. The pattern is **opt-in**: enable
it inside `ConfigureRetryDlq`. When it is not enabled, deferring a message is not supported and a
rejected message is logged without its offset being stored.

```csharp
builder.Services.AddBareWireKafka(kafka =>
{
    kafka.BootstrapServers("localhost:9092");
    kafka.ConsumerGroup("order-processing");
    kafka.ConfigureRetryDlq(retry =>
    {
        retry.Enable();
        retry.MaxRetries(5);
        retry.Backoff(TimeSpan.FromSeconds(1), multiplier: 2.0, TimeSpan.FromMinutes(1));
    });
});
```

A failed message is republished to a **retry-topic** (with exponential backoff) and, once retries
are exhausted or the message is rejected, to a **DLQ-topic**. The retry-topic and DLQ-topic names
are derived from the source topic by appending a suffix.

| Method | Purpose | Default |
|--------|---------|---------|
| `Enable()` | Activates the pattern (required to opt in) | disabled |
| `MaxRetries(int)` | Retry attempts before dead-lettering | `3` |
| `RetryTopicSuffix(string)` | Suffix appended to form the retry-topic name | `.retry` |
| `DlqTopicSuffix(string)` | Suffix appended to form the DLQ-topic name | `.DLQ` |
| `Backoff(TimeSpan, double, TimeSpan)` | Base delay, multiplier, and max-delay cap for retry backoff | `1s`, `2.0`, `5m` |

## Topology

BareWire uses **manual topology** by default. In Kafka, the only actionable declaration is a
**topic** — Kafka has no exchange/binding concept, so exchange and binding declarations are
accepted (to satisfy the shared topology contract) but are silently ignored at deploy time.

Because the standard RabbitMQ-flavoured queue arguments (dead-letter, TTL, and so on) have no
native Kafka equivalent, Kafka-specific topic parameters use the `bw.kafka.*` argument convention,
supplied through the queue configurator's `Argument` escape hatch:

```csharp
topology.DeclareQueue("orders", durable: true, autoDelete: false, configure: q =>
{
    q.Argument("bw.kafka.partitions", 6)
     .Argument("bw.kafka.replication-factor", 3)
     .Argument("bw.kafka.retention.ms", 604_800_000); // 7 days
});
```

A `bw.kafka.config.<x>` key is forwarded to the topic's broker-side `Configs["<x>"]`.

## Configuration reference

| Method (`IKafkaConfigurator`) | Purpose | Default |
|-------------------------------|---------|---------|
| `BootstrapServers(string)` | Comma-separated `host:port` broker list | required |
| `ConsumerGroup(string)` | Consumer group id | required to consume |
| `ConsumerAutoOffsetReset(AutoOffsetReset)` | Offset reset policy when no committed offset exists | `Earliest` |
| `ConsumerPartitionAssignmentStrategy(KafkaPartitionAssignmentStrategy)` | Partition assignment / rebalance strategy | `CooperativeSticky` |
| `ConfigureRetryDlq(Action<IKafkaRetryDlqConfigurator>)` | Retry-topic + DLQ-topic pattern | disabled (opt-in) |

## See also

- [API reference](../api/index.md)
- [Configuration](configuration.md)
- [Topology](topology.md)
