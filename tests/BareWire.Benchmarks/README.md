# BareWire Benchmarks

Performance benchmarks for BareWire messaging pipeline using [BenchmarkDotNet](https://benchmarkdotnet.org/).

## Performance Targets

| Operation | Throughput | Allocation | Tolerance |
|-----------|-----------|------------|-----------|
| Publish typed (in-memory) | > 500K msgs/s | < 768 B/msg | ±10% |
| Publish raw (in-memory) | > 1M msgs/s | < 512 B/msg | ±10% |
| Consume + ack (in-memory) | > 300K msgs/s | < 512 B/op | ±10% |
| SAGA transition (in-memory) | > 100K msgs/s | < 768 B/transition | ±15% |
| JSON serialize raw (1 KB) | < 2 μs | < 384 B | ±10% |
| JSON serialize envelope (1 KB) | < 2 μs | < 512 B | ±10% |
| JSON deserialize raw (1 KB) | < 5 μs | < 5 KB | ±10% |
| JSON deserialize envelope (1 KB) | < 10 μs | < 8 KB | ±10% |

## Running Benchmarks

```bash
# List all available benchmarks
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*' --list flat

# Run all benchmarks
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*'

# Run specific benchmark class
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*Publish*'
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*Consume*'
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*Saga*'

# JSON vs MessagePack comparative benchmark (R3.3)
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*JsonVsMessagePack*'

# Export results (JSON + CSV + Markdown)
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*' --exporters json csv markdown

# Export to specific directory
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*' --exporters json csv markdown --artifacts ./benchmark-results
```

## Benchmark Classes

| Class | Description | Targets |
|-------|-------------|---------|
| `PublishBenchmarks` | Typed and raw publish through in-memory transport | 500K–1M msgs/s |
| `ConsumeBenchmarks` | Consume + ack loop via InMemoryTransportAdapter | > 300K msgs/s |
| `SagaBenchmarks` | State machine transitions with InMemorySagaRepository | > 100K msgs/s |
| `SerializationBenchmarks` | JSON serialize/deserialize with System.Text.Json | < 1 μs |
| `JsonVsMessagePackBenchmarks` | JSON vs MessagePack serialize/deserialize/on-wire size, same object graph, 100 B – 100 KB | MessagePack ~2-5x fewer allocations |

## JSON vs MessagePack Comparison (R3.3)

`JsonVsMessagePackBenchmarks` compares the raw System.Text.Json serializer
(`application/json`) against the MessagePack serializer (`application/x-msgpack`,
see [ADR-013](../../docs/architecture/decisions/ADR-013-messagepack-zero-copy-serializer.md))
over the **same object graph**, parametrized by `PayloadSizeBytes` ∈ {100, 1 000, 10 000, 100 000}.

Six `[Benchmark]` methods: `Serialize_{Json,MsgPack}`, `Deserialize_{Json,MsgPack}`,
and `SerializedSize_{Json,MsgPack}` (on-wire byte count). `Serialize_Json` is the
`[Benchmark(Baseline = true)]`, so the `Ratio` and `Alloc Ratio` columns are relative
to it.

**How to read the report:**

- The **primary metric is allocations** (`Allocated` / `Alloc Ratio`), not on-wire size.
  Documented expectation (TASKS-ROADMAP R3.3): MessagePack allocates **~2-5x less**,
  most visibly on the **serialize** path — MessagePack writes directly into the pooled
  `IBufferWriter<byte>` with no intermediate `byte[]` (zero-copy, ADR-003), so its
  serialize allocation is typically reported as `-` (≈0 B), versus the `Utf8JsonWriter`
  overhead on the JSON path. On the **deserialize** path the gap is narrower because the
  allocation is dominated by the resulting object graph, which is identical for both
  serializers.
- `PayloadSizeBytes` is the payload size **measured against JSON** for a shared object
  graph (consistent with `SerializationBenchmarks`). For a given size the MessagePack
  payload is naturally smaller — that smaller on-wire footprint is exactly what
  `SerializedSize_MsgPack` reports. So read "for an object graph that serializes to
  ~N B in JSON, MessagePack occupies fewer bytes", not "MessagePack has N B at size N".
- The comparison uses the safe production options (`BareWireMessagePackSerializerOptions.Default`:
  `UntrustedData`, `ContractlessStandardResolver`, no LZ4, no Typeless) — the reported
  numbers are not from an unsafe fast path.
- The `2-5x` figure is a **directional documented expectation**, not a pass/fail gate
  (this benchmark is a measurement artifact, not a unit test).

## Interpreting Results

BenchmarkDotNet reports:
- **Mean** — average execution time per operation
- **Allocated** — bytes allocated per operation (from `[MemoryDiagnoser]`)
- **Gen0/Gen1/Gen2** — GC collections per 1000 operations

Key metrics to watch:
- **Allocated** should stay within targets above
- **Gen2** should be 0 in steady-state (zero Gen2 GC pressure per ADR-003)
- **Mean** converted to ops/s should exceed throughput targets

## Notes

- `[EventPipeProfiler]` is intentionally omitted due to BenchmarkDotNet bug with .NET 10
  ([dotnet/BenchmarkDotNet#2699](https://github.com/dotnet/BenchmarkDotNet/issues/2699)).
  Re-enable after the fix ships.
- RabbitMQ benchmarks are deferred to post-MVP. In-memory benchmarks validate the core pipeline.
- CI runs benchmarks with `continue-on-error: true` — results are uploaded as artifacts for manual review.
