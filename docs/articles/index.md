# BareWire Documentation

Welcome to the BareWire documentation. BareWire is a high-performance async messaging library for .NET 10 / C# 14 — an alternative to MassTransit built around raw-first serialization, zero-copy pipelines, manual topology, and deterministic memory usage.

## Table of Contents

1. [Getting Started](getting-started.md) — installation, first publisher and consumer
2. [Configuration](configuration.md) — bus setup, transport registration, DI
3. [Publishing and Consuming](publishing-and-consuming.md) — publish/subscribe, request-response, raw messages
4. [Topology](topology.md) — exchanges, queues, bindings, routing keys
5. [Consumer Routing Keys](consumer-routing-keys.md) — multiple consumers on one queue selected by routing-key pattern, type-less interop
6. [Per-Key Consumer Ordering](per-key-ordering.md) — ordered consumption per key across competing consumers
7. [Flow Control and Backpressure](flow-control.md) — credit-based flow control, publish-side backpressure
8. [Retry and Dead Letter Queues](retry-and-dlq.md) — retry policies, DLX routing, DLQ consumers
9. [Custom Serializers](custom-serializers.md) — per-endpoint serializer and deserializer overrides
10. [MessagePack Serialization](serialization-messagepack.md) — compact binary serialization with content-type routing
11. [CloudEvents](cloudevents.md) — binary and structured CloudEvents 1.0 envelopes
12. [Saga State Machines](saga.md) — state machines, compensable activities, scheduled timeouts
13. [Redis Saga Persistence](saga-redis.md) — Redis-backed saga repository with optimistic concurrency
14. [Transactional Outbox](outbox.md) — effectively-once delivery (at-least-once + inbox dedup) via transactional outbox
15. [Inbox Deduplication](inbox.md) — preventing duplicate message processing
16. [Observability](observability.md) — OpenTelemetry, metrics, health checks
17. [Aspire Integration](aspire-integration.md) — orchestrating BareWire apps with .NET Aspire
18. [Transports](transports.md) — transport-agnostic core; choosing and registering an adapter
    - [RabbitMQ Transport](transport-rabbitmq.md) — the reference transport: registration, connection, TLS/mTLS, settlement, feature map
    - [Kafka Transport](transport-kafka.md) — idempotent producer, consumer groups, retry/DLQ topics
    - [Azure Service Bus Transport](transport-azure-service-bus.md) — sessions, scheduled messages, Entra ID + SAS
    - [AWS SQS Transport](transport-aws-sqs.md) — batch producer, long-polling, FIFO, IAM, SSE, redrive DLQ
    - [Google Pub/Sub Transport](transport-google-pubsub.md) — ordering keys, dead-letter topics
19. [MassTransit Interop](masstransit-interop.md) — consuming and publishing MassTransit envelope messages; bus-global, per-endpoint, and per-consumer (`UseMassTransitEnvelope()`) opt-in
20. [Advanced Patterns](advanced-patterns.md) — partitioning, multi-consumer endpoints, raw interop
21. [Benchmark Report](benchmark-report.md) — throughput and allocation results against the performance targets

## Allocation Characteristics

- **`PublishRawAsync` (pre-serialized passthrough)** — constant **136 B** regardless of payload size (100 B → 10 KB). The pre-serialized `ReadOnlyMemory<byte>` passes through without copying.
- **PublishTyped** — **~544 B fixed overhead + serialized payload size**. The serialization boundary copy (`.ToArray()`) is architecturally required — `OutboundMessage` must outlive the pooled writer scope.
- **Serialization (raw)** — constant **448 B** regardless of payload size. `PooledBufferWriter` rents from `ArrayPool<byte>.Shared` (zero-copy pipeline).

> The numbers above are the steady-state allocation characteristics. The throughput-oriented
> benchmark suite reports per-message figures measured a different way (e.g. PublishTyped 600 B,
> PublishRaw 341 B per message) — see the full [benchmark report](benchmark-report.md) for both
> the throughput table and the payload-scaling table.

## Samples

All documentation references working code from the `samples/` directory. You can run all samples simultaneously using the Aspire AppHost:

```bash
dotnet run --project samples/BareWire.Samples.AppHost/
```

| Sample | Description |
|---|---|
| `BasicPublishConsume` | Publish/subscribe with PostgreSQL, retry (3x) and dead letter queue |
| `RequestResponse` | Synchronous request-response with validation |
| `RawMessageInterop` | Interop with legacy systems via raw JSON (Raw + Typed consumers) |
| `RabbitMQ` | Orders with transactional outbox (SQLite) and publish backpressure |
| `BackpressureDemo` | Consume-side and publish-side flow control under load |
| `RetryAndDlq` | Retry policies, DLX routing, DLQ consumer with PostgreSQL persistence |
| `SagaOrderFlow` | Order lifecycle saga with compensation and finalization |
| `TransactionalOutbox` | Exactly-once delivery via transactional outbox with EF Core |
| `InboxDeduplication` | Inbox deduplication across multiple consumers (Email + Audit) |
| `ObservabilityShowcase` | 3-hop distributed tracing (order → payment → shipment) with OTel |
| `MultiConsumerPartitioning` | Single-instance per-key ordering via `OrderedByHeader("ordering-key")` (fixed-lane) |
| `OrderedConsumers` | End-to-end per-key consumer ordering across competing instances (SAC + LocalPartitioned), outbox `PerKey`, poison-head parking — see [Per-Key Consumer Ordering](per-key-ordering.md) |
| `MassTransitInterop` | Coexistence of BareWire and MassTransit producers on shared RabbitMQ |
