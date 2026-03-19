# BareWire Benchmarks

Performance benchmarks for BareWire messaging pipeline using [BenchmarkDotNet](https://benchmarkdotnet.org/).

## Performance Targets

| Operation | Throughput | Allocation | Tolerance |
|-----------|-----------|------------|-----------|
| Publish typed (in-memory) | > 500K msgs/s | < 128 B/msg | ±10% |
| Publish raw (in-memory) | > 1M msgs/s | 0 B/msg | ±10% |
| Consume + ack (in-memory) | > 300K msgs/s | < 256 B/msg | ±10% |
| SAGA transition (in-memory) | > 100K msgs/s | < 512 B/msg | ±15% |
| JSON serialize (1 KB) | < 1 μs | < 128 B | ±10% |
| JSON deserialize (1 KB) | < 1 μs | < 256 B | ±10% |

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
