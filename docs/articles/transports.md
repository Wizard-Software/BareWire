# Transports

BareWire separates its **core engine** — pipeline, flow control, dispatch, saga, and outbox — from the
**transport adapter** that speaks a broker's wire protocol. The same consumers, publishers, and
configuration work across every transport: you swap the adapter, not your application code.

RabbitMQ is the reference transport and the focus of most guides on this site. BareWire also ships
first-class adapters for Kafka, Azure Service Bus, AWS SQS, and Google Pub/Sub.

## Available transports

| Transport | Bundle package | Single-call registration | Highlights |
|---|---|---|---|
| RabbitMQ | `BareWire.RabbitMQ` | `AddBareWireWithRabbitMq` | Exchanges, queues, bindings, consistent-hash, single-active-consumer, TLS/mTLS — [RabbitMQ Transport](transport-rabbitmq.md) |
| Kafka | `BareWire.Kafka` | `AddBareWireWithKafka` | Idempotent producer, consumer groups, retry/DLQ topics — [Kafka Transport](transport-kafka.md) |
| Azure Service Bus | `BareWire.AzureServiceBus` | `AddBareWireWithAzureServiceBus` | Sessions (per-session FIFO), scheduled messages, Entra ID + SAS — [Azure Service Bus Transport](transport-azure-service-bus.md) |
| AWS SQS | `BareWire.AWS.SQS` | `AddBareWireWithSqs` | Batch producer, long-polling, FIFO, IAM auth, SSE, redrive DLQ — [AWS SQS Transport](transport-aws-sqs.md) |
| Google Pub/Sub | `BareWire.Google.PubSub` | `AddBareWireWithPubSub` | Ordering keys, dead-letter topics — [Google Pub/Sub Transport](transport-google-pubsub.md) |

> **Kafka maturity caveat.** The Kafka adapter currently defaults to `SecurityProtocol=Plaintext`
> and SASL/SSL is not yet wired up — do not point it at a production broker until the secure-config
> layer lands. See [Kafka Transport](transport-kafka.md) for details.

## Registering a transport

Every transport offers the same two registration shapes described in [Configuration](configuration.md):

- **Bundle (recommended)** — one package, one call: `AddBareWireWith{Transport}(transport => …, bus => …)`.
- **Two calls** — register the transport adapter and the core separately with `AddBareWire{Transport}(…)`
  followed by `AddBareWire(…)`. Use this form when an application hosts **more than one transport**.

```csharp
// Bundle — the common single-transport case
builder.Services.AddBareWireWithKafka(
    transport =>
    {
        transport.BootstrapServers("localhost:9092");
        transport.ConsumerGroup("orders");
    },
    bus => bus.AddConsumer<OrderConsumer>());
```

## Serialization and persistence are pluggable too

The wire format and the stateful stores are independent of the transport:

- **Serialization** — raw JSON by default; opt into [MessagePack](serialization-messagepack.md) for compact
  binary, [CloudEvents](cloudevents.md) for a standard event envelope, or your own format
  ([Custom Serializers](custom-serializers.md)).
- **Saga persistence** — [Redis](saga-redis.md) is available alongside the EF Core store (see [Saga](saga.md)).

## See also

- [Configuration](configuration.md) — registration patterns in depth
- [Custom Serializers](custom-serializers.md)
- [API Reference](../api/index.md)
