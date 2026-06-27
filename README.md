# BareWire

High-performance async messaging library for .NET 10 / C# 14.

An alternative to MassTransit with a fundamentally different architecture: **raw-first** (no envelope by default), **zero-copy pipeline**, **manual topology**, and **deterministic memory usage**.

## Key Design Principles

- **Raw-first** — default serializer produces raw JSON, no envelope. Envelope format is opt-in.
- **Manual topology** — `ConfigureConsumeTopology = false` by default. Auto-topology is opt-in.
- **Zero-copy pipeline** — `IBufferWriter<byte>` / `ReadOnlySequence<byte>` with `ArrayPool`. No `byte[]` allocation per-message in hot paths.
- **Credit-based flow control** — bounded channels, atomic inflight tracking, health alerts at 90% capacity.
- **Familiar API** — uses MassTransit naming conventions (`IBus`, `IConsumer<T>`, `ConsumeContext<T>`) for easy migration.

## Packages

| Package | Description |
|---|---|
| `BareWire.Abstractions` | Public interfaces, zero dependencies |
| `BareWire` | Core engine, pipeline, bus implementation |
| `BareWire.Serialization.Json` | JSON serializer (System.Text.Json) |
| `BareWire.Serialization.MsgPack` | MessagePack serializer (compact binary) |
| `BareWire.CloudEvents` | CloudEvents 1.0 envelope (binary + structured) |
| `BareWire.Transport.RabbitMQ` | RabbitMQ transport |
| `BareWire.Transport.Kafka` | Kafka transport |
| `BareWire.Transport.AzureServiceBus` | Azure Service Bus transport |
| `BareWire.Transport.AWS.SQS` | AWS SQS transport |
| `BareWire.Transport.Google.PubSub` | Google Pub/Sub transport |
| `BareWire.Saga` | SAGA state machine |
| `BareWire.Saga.Redis` | Redis SAGA persistence |
| `BareWire.Outbox` | Outbox/Inbox pattern |
| `BareWire.Observability` | OpenTelemetry integration |
| `BareWire.Testing` | In-memory test harness |

> Each transport also ships a single-call **bundle** package — `BareWire.RabbitMQ`, `BareWire.Kafka`,
> `BareWire.AzureServiceBus`, `BareWire.AWS.SQS`, `BareWire.Google.PubSub` — that registers the core and
> the transport in one `AddBareWireWith{Transport}` call.

## Build & Test

```bash
# Build
dotnet build BareWire.slnx

# All tests
dotnet test BareWire.slnx

# Unit tests only
dotnet test tests/BareWire.UnitTests/

# Benchmarks
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*Publish*'
```

## Performance Targets

- < 768 B/msg publish allocation, < 256 B/msg consume allocation
- \> 500K msgs/s publish throughput
- \> 300K msgs/s consume throughput (in-memory transport)

### Allocation characteristics

- **PublishRaw** — constant **136 B** regardless of payload size (100 B → 10 KB, no difference). Pre-serialized `ReadOnlyMemory<byte>` is passed through without copying.
- **PublishTyped** — **~544 B fixed overhead + serialized payload size**. The serialization boundary copy (`.ToArray()` in `MessagePipeline.ProcessOutboundAsync`) is architecturally required — `OutboundMessage` must outlive the pooled writer scope.
- **Serialization (raw)** — constant **448 B** regardless of payload size. `PooledBufferWriter` rents from `ArrayPool<byte>.Shared`, confirming ADR-003 zero-copy pipeline.

Full report: [docs/articles/benchmark-report.md](docs/articles/benchmark-report.md)

## License

MIT. See [LICENSE](LICENSE).
