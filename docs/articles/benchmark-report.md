# BareWire Benchmark Report

**Date**: 2026-09-25
**Runtime**: .NET 10.0.12 (Arm64 RyuJIT)
**Hardware**: Apple M3 Max, 14 cores (performance + efficiency cores), macOS 27.0
**GC**: Concurrent Workstation
**BenchmarkDotNet**: v0.15.8
**Job**: `launchCount: 1, warmupCount: 3, iterationCount: 15` (declared on each benchmark class)

---

## Baseline change

This report uses a **new measurement baseline**. The previous report's numbers are **not comparable**
with the numbers below.

- **The Core targets now measure Core only.** The in-memory stub in `BareWire.Testing` that the old
  publish and consume benchmarks ran on has been removed. The test harness now runs on the real in-memory
  transport engine. Measuring the Core targets through it would have silently redefined them as
  "Core + transport engine". The Core benchmarks therefore run on a minimal fake adapter local to the
  benchmark project. Its send is a sink that confirms each message without storing or copying it. Its
  consume side is a bounded channel, filled outside the measured region.
- **The in-memory transport engine is measured separately**, by the `InMemoryTransportBenchmarks` and
  `InMemoryFanOutBenchmarks` classes (see [In-memory transport](#in-memory-transport)).
- **Payload.** The Core publish path now serializes a fixed ~56 B JSON payload. The old serializer wrote an
  empty body.
- **Cost removed from the Core baseline.** The fake adapter no longer pays the old stub's per-message
  costs: a message id string, an inbound message, and a result array.
- **Consume unit.** `ConsumeAndAck_CoreOnly`, formerly `ConsumeAndAck_InMemory`, now reports **per
  message**. The old report gave the time for a batch of 1,000 messages.
- **Consume allocation target.** The target is **< 512 B/op**, as the project instructions define. The old
  report used "< 256 B/msg".
- **Targets are unchanged.** Every gap below is reported as it was measured.

**Verdict rule.** Throughput is derived as `1 / Mean`. A benchmark gets **PASS** or **MISS** only when the
whole interval `Mean ± Error` (99.9% confidence) is on one side of the target. Otherwise it is marked
**inconclusive**. Allocation is deterministic per operation, so it is compared with the target directly.

---

## Summary

### Core-only (targets from the project instructions)

| Benchmark | Target | Actual (Mean ± Error) | Throughput | Allocation target | Actual alloc | Status |
|-----------|--------|-----------------------|------------|-------------------|--------------|--------|
| PublishTyped | > 500K msgs/s | 658.6 ± 13.6 ns | ~1.52M msgs/s | < 768 B/msg | 582 B | PASS / PASS |
| PublishRaw | > 1M msgs/s | 222.7 ± 5.7 ns | ~4.49M msgs/s | < 512 B/msg | 92 B | PASS / PASS |
| ConsumeAndAck_CoreOnly | > 300K msgs/s | 104.3 ± 8.6 ns | ~9.6M msgs/s | < 512 B/op | 0 B | PASS / PASS (floor, see note) |
| SagaTransition (1 instance) | > 100K trans/s | 3.07 ± 1.24 µs | ~326K trans/s | < 768 B/transition | 560 B | PASS / PASS |

**`ConsumeAndAck_CoreOnly` is a floor, not a pipeline measurement.** The benchmark only dequeues from the
fake adapter's channel and acknowledges each message. No receive pipeline, dispatch or DI scope runs, which
is why it allocates nothing. The per-message cost of the Core receive and dispatch path appears in the
ordered consume benchmark's `Baseline_Off` rows below. That path misses the < 512 B/op budget.

### In-memory transport, single binding (same targets, used as a gate)

| Benchmark | Target | Actual (Mean ± Error) | Throughput | Allocation target | Actual alloc | Status |
|-----------|--------|-----------------------|------------|-------------------|--------------|--------|
| Publish_SingleBinding | > 500K msgs/s | 877.1 ± 12.0 ns | ~1.14M msgs/s | < 768 B/msg | 776 B (758 B in a later run) | PASS / **borderline** (MISS by 8 B in the recorded run) |
| Consume_SingleBinding | > 300K msgs/s | 573.5 ± 48.9 ns | ~1.74M msgs/s | < 512 B/op | 153 B | PASS / PASS |

**Overall:**

- Every throughput target is met.
- Every Core-only allocation target is met.
- **One gate is borderline.** The in-memory transport publish path allocated 776 B/msg in the recorded run,
  8 B (about 1%) above the 768 B/msg target: a **MISS** against the strict target. A later verification run
  of the same benchmark measured 758 B/msg, just under the target, so the result is not stable across runs.
  It sits on the target, inside the ±10% tolerance in the benchmark project's README, and is not a clear
  pass.
- The Core receive and dispatch path costs about 1.9 KB per message (see the ordered consume section).

---

## Detailed results

### 1. Core-only publish

| Method | Mean | Error | StdDev | Gen0 | Allocated |
|--------|------|-------|--------|------|-----------|
| PublishTyped | 658.6 ns | 13.57 ns | 12.03 ns | 0.0696 | 582 B |
| PublishRaw | 222.7 ns | 5.68 ns | 5.31 ns | 0.0110 | 92 B |

- **What `PublishTyped` measures.** Typed publish through the full outbound pipeline, including the
  ~56 B serialized payload and the body copy that must outlive the pooled writer.
- **Where the transport step happens.** The bus hands messages to the fake sink on its background publish
  loop, in batches of up to 64.
- **Raw publish scaling.** Raw publish with a pre-serialized payload allocates a constant **56 B** for
  payloads of 100 B, 1 KB and 10 KB alike:

| PayloadSizeBytes | Mean | Error | Allocated |
|------------------|------|-------|-----------|
| 100 | 1.559 µs | 0.553 µs | 56 B |
| 1,000 | 1.714 µs | 0.623 µs | 56 B |
| 10,000 | 1.365 µs | 0.227 µs | 56 B |

The scaling benchmark rebuilds its payload in an iteration setup. That leaves one invocation per iteration,
so its times are imprecise (wide error). Its allocation column is exact.

### 2. Core-only consume

| Method | Mean (per message) | Error | StdDev | Allocated |
|--------|--------------------|-------|--------|-----------|
| ConsumeAndAck_CoreOnly | 104.3 ns | 8.62 ns | 7.64 ns | 0 B |

The measured path consumes 1,000 prebuilt messages and acknowledges each one: a bounded-channel dequeue plus
the settle call. The channel is refilled in an iteration setup, so there is one invocation per iteration,
which lowers precision.

### 3. Ordered consume (Core receive and dispatch path)

The benchmark calls `ReceiveEndpointRunner` directly, with a raw consumer, on the Core-only fake adapter.
All messages are enqueued before the runner starts. Building the message headers and inbound messages is
still inside the measured method, as before. What is new is one bounded-channel read per message.

| MessageCount | LaneCount | OrderedBy_On | Baseline_Off | Ratio | Alloc On | Alloc Off |
|--------------|-----------|--------------|--------------|-------|----------|-----------|
| 500 | 1 | 618.8 µs | 1,747.3 µs | 0.35 | 904.47 KB | 932.07 KB |
| 500 | 4 | 2,195.3 µs | 1,742.9 µs | 1.26 | 1,039.37 KB | 932.06 KB |
| 500 | 8 | 2,442.2 µs | 1,819.9 µs | 1.34 | 1,021.64 KB | 932.14 KB |
| 2,000 | 1 | 1,836.4 µs | 6,837.8 µs | 0.27 | 3,550.32 KB | 3,710.28 KB |
| 2,000 | 4 | 8,731.5 µs | 6,826.1 µs | 1.28 | 4,145.07 KB | 3,710.30 KB |
| 2,000 | 8 | 9,860.7 µs | 7,000.3 µs | 1.41 | 4,031.27 KB | 3,710.32 KB |

**Per-message slope (2,000 minus 500 messages, divided by 1,500):**

| Path | Time | Throughput | Allocation |
|------|------|------------|------------|
| `Baseline_Off` | ~3.39 µs/msg | ~295K msgs/s | ~1.85 KB/msg |
| `OrderedBy_On` with 4 lanes | ~4.36 µs/msg | — | ~2.07 KB/msg |

**Findings:**

- **The ordered path's < 512 B/op allocation goal is missed.** About 0.4–0.5 KB/msg of the slope is the
  benchmark's own message construction: a two-entry header dictionary, a message id string, the inbound
  message, and channel growth. That share is an estimate, not a measurement. The rest is the Core receive
  path: DI scope, consume context and dispatch.
- **The claim that per-lane overhead is constant holds for 4 and 8 lanes.** The `Alloc Ratio` stays between
  1.09 and 1.12 at both message counts. With 1 lane, ordering ON is faster than the sequential baseline and
  allocates slightly less.

### 4. Saga state transitions (no transport involved)

| Instances | Mean | Per transition | Throughput | Allocated / transition |
|-----------|------|----------------|------------|------------------------|
| 1 | 3.069 µs | 3.069 µs | ~326K/s | 560 B |
| 10 | 13.386 µs | 1.339 µs | ~747K/s | ~767 B |
| 100 | 97.413 µs | 974 ns | ~1.03M/s | ~724 B |

- **Throughput and single-instance allocation:** PASS on both.
- **Batched allocation is noisy.** The class ran a second time in the in-memory filter run. That run
  measured about 560 B/transition at 10 instances and about 633 B/transition at 100. The allocation per
  transition in a batch therefore varies between runs because of the in-memory saga repository's copying.
- **Precision is limited.** The class uses an iteration setup, so there is one invocation per iteration.

---

## In-memory transport

### Single binding

- **`Publish_SingleBinding`.** Publishes 256 typed messages through the bus with the same ~56 B payload.
  The timed window stays open until the queue actually holds all 256 messages. The transport's routing,
  buffer rent, payload copy and enqueue therefore all happen inside the measurement. The queue is drained
  in an iteration cleanup, and the benchmark checks that the queue is empty before the next iteration.
- **`Consume_SingleBinding`.** Consumes 1,000 queued messages, acknowledges each one and returns its pooled
  buffer. The queue is filled in an iteration setup, which gives one invocation per iteration and lower
  precision.

| Method | Mean | Error | StdDev | Allocated |
|--------|------|-------|--------|-----------|
| Publish_SingleBinding | 877.1 ns | 11.96 ns | 9.98 ns | 776 B |
| Consume_SingleBinding | 573.5 ns | 48.89 ns | 43.34 ns | 153 B |

**Compare each against the targets, not against each other.**

- `Publish_SingleBinding` is a barrier batch of 256 messages.
- The Core-only `PublishTyped` is one call in steady state, and its transport step runs outside the call.
- Subtracting one from the other would not isolate the transport cost.

### Fan-out (budget per delivered copy)

**Setup:**

- One fanout exchange is bound to K queues, K ∈ {1, 4, 16}.
- Payload is 128 B or 4,096 B.
- One consumer per queue runs for the whole benchmark. It acknowledges every copy and disposes it, which
  returns the pooled buffer.

**Each invocation:**

- Sends 16 messages.
- Waits until all 16 × K copies are acknowledged. The last acknowledging consumer signals completion, so
  no poller runs in the timed window.

**Batch size.** At K = 16 at most 256 buffers are in flight at once. That is within the shared array pool's
retention on the measured 14-core machine, so allocation does not include pool misses. The per-copy
allocation is nearly the same at 128 B and at 4,096 B, which confirms this. Pool retention is per core, so a
machine with fewer cores may retain fewer buffers.

**How the table is computed.** BenchmarkDotNet reports per published message. The per-copy columns divide
that value by K.

| FanOut | Payload | Mean / message | Error | Allocated / message | **Time / copy** | **Alloc / copy** |
|--------|---------|----------------|-------|---------------------|-----------------|------------------|
| 1 | 128 B | 810.7 ns | 49.97 ns | 480 B | 810.7 ns | 480 B |
| 1 | 4,096 B | 889.2 ns | 44.89 ns | 493 B | 889.2 ns | 493 B |
| 4 | 128 B | 4,222.9 ns | 99.79 ns | 1,434 B | 1,055.7 ns | 359 B |
| 4 | 4,096 B | 4,648.8 ns | 196.91 ns | 1,435 B | 1,162.2 ns | 359 B |
| 16 | 128 B | 16,678.1 ns | 701.47 ns | 4,985 B | 1,042.4 ns | 312 B |
| 16 | 4,096 B | 16,911.4 ns | 538.05 ns | 4,988 B | 1,057.0 ns | 312 B |

**Findings:**

- **Allocation per copy falls as fan-out grows,** from 480 B to 312 B. The fixed per-message cost is shared
  across more copies, and each extra copy adds about 300 B: (4,985 − 480) / 15.
- **Allocation does not depend on payload size** (within 13 B at K = 1 and within 3 B at K = 4 and 16).
  Payload bytes live in pooled buffers.
- **This run was contended.** The fan-out class was re-run on its own after a fix to how an invocation
  signals completion. During that run the load average rose to 13, so its error bars are wider. The
  earlier run of the same class gave the same allocation per copy and times per copy within about 10%.

### Shared reference-counted buffer: decision

**Question.** Should fan-out share one reference-counted payload buffer across all copies, instead of
copying the payload into a pooled buffer per queue?

**Criterion.** Measured at K = 16, with time per copy `t`:

- Copying dominates when `Δ = t_4096 − t_128` is at least half of `t_4096`.
- The difference between the two means must also exceed the sum of their errors.

**Measured:**

| Quantity | Value |
|----------|-------|
| `t_128` | 1,042.4 ns per copy |
| `t_4096` | 1,057.0 ns per copy |
| `Δ` | ~15 ns per copy, about 1.4% of `t_4096` |
| Significance | the means differ by 233 ns against a summed error of 1,240 ns, so the difference is not significant |
| Upper bound on `Δ` (99.9%) | (233 + 1,240) / 16 ≈ 92 ns per copy, about 9% of `t_4096` |
| Earlier run (before the completion-signal fix) | `Δ` ≈ 102 ns per copy (9%); significant, still far below the threshold |
| Estimated saving of a shared buffer | at most (K − 1) / K × 92 ns ≈ 86 ns per copy, under 9% of the per-copy time |

**Decision: not now.** Copying the payload does not dominate the fan-out cost, even at 4 KB and 16 copies.
Per-copy routing, enqueue, delivery and acknowledgement dominate. A shared reference-counted buffer would
add ownership complexity to the settle and dead-letter paths for a gain that is under 10% even at its upper
bound. Revisit if payloads
much larger than 4 KB with high fan-out become a real workload.

---

## Measurement conditions and caveats

- **Machine.** Developer laptop, Apple M3 Max, 14 cores. BenchmarkDotNet could not raise the process
  priority (permission denied).
- **Mixed core types.** The machine has performance and efficiency cores. The OS may schedule benchmark
  threads on either, which adds variance, especially for the multi-threaded fan-out and ordered benchmarks.
- **Background load.** Before each run the 1-minute load average was checked and allowed to settle below 4:
  it was 3.7 to 4.0 at the start of each run. Another build job on the same machine may have been active
  during the runs. The final fan-out re-run was clearly contended: the load average reached 13 and the run
  took about ten times longer than before. No heavy `dotnet` process was busy at the start of any run, but some interference cannot
  be ruled out.
- **One invocation per iteration.** Classes with an iteration setup or cleanup run a single invocation per
  iteration: `ConsumeBenchmarks`, `SagaBenchmarks`, `PublishPayloadScalingBenchmarks`,
  `InMemoryTransportBenchmarks`. Their means carry wider error bars than the steady-state classes.
- **Harness overhead.** Inside the timed window, the fan-out benchmark allocates one completion source per
  invocation and the state-machine box of the suspended benchmark method: about 10–20 B per published
  message. Its watchdog allocates about once per second. The single-binding publish barrier polls the queue's
  occupancy without allocating.

---

## Environment

```
BenchmarkDotNet v0.15.8, macOS 27.0 [Darwin 27.0.0]
Apple M3 Max, 1 CPU, 14 logical and 14 physical cores
Runtime: .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
GC: Concurrent Workstation
HardwareIntrinsics: ArmBase+AdvSimd,AES,CRC32,DP,RDM,SHA1,SHA256 VectorSize=128
Job: LaunchCount=1, WarmupCount=3, IterationCount=15
```

Reproduce:

```bash
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*.PublishBenchmarks.*' '*PublishPayloadScaling*' '*.ConsumeBenchmarks.*' '*SagaBenchmarks*'
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*OrderedConsumeBenchmarks*'
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*InMemory*'
```
