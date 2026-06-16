# BareWire.Transport.Kafka

Kafka transport provider for BareWire with idempotent producer and ordering key support.

## Installation

```bash
dotnet add package BareWire.Transport.Kafka
```

## Usage

```csharp
builder.Services.AddBareWireKafka(kafka =>
{
    kafka.BootstrapServers("localhost:9092");
    kafka.ConsumerGroup("order-processing");
    kafka.ConsumerAutoOffsetReset(AutoOffsetReset.Earliest);
    kafka.ConsumerPartitionAssignmentStrategy(KafkaPartitionAssignmentStrategy.CooperativeSticky);
});
```

### Retry-topic + DLQ-topic pattern (opt-in)

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

## Features

- Idempotent producer (exactly-once delivery semantics on the produce path)
- Consumer groups with partition assignment (cooperative-sticky by default)
- Retry-topic and DLQ-topic pattern (opt-in, ADR-010)
- Ordering preserved via message key → partition mapping
- Manual topology by default — topics are the only actionable declaration (Kafka has no exchange/binding concept); topic parameters use the `bw.kafka.*` argument convention

## Documentation

Full documentation: [barewire.wizardsoftware.pl](https://barewire.wizardsoftware.pl)

## License

MIT
