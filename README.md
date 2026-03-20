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
| `BareWire.Core` | Core engine, pipeline, bus implementation |
| `BareWire.Serialization.Json` | JSON serializer (System.Text.Json) |
| `BareWire.Transport.RabbitMQ` | RabbitMQ transport |
| `BareWire.Saga` | SAGA state machine |
| `BareWire.Outbox` | Outbox/Inbox pattern |
| `BareWire.Observability` | OpenTelemetry integration |
| `BareWire.Testing` | In-memory test harness |

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

- < 256 B/msg allocation
- \> 500K msgs/s publish throughput
- \> 300K msgs/s consume throughput (in-memory transport)

## License

Proprietary. All rights reserved.
