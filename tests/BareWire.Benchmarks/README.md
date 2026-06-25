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
| `OrderedConsumeBenchmarks` | Ordered consume path (`OrderedBy` ON vs OFF) — N×L params sweep; per-lane overhead constant (ADR-026 R8.15) | < 512 B/op |
| `SagaBenchmarks` | State machine transitions with InMemorySagaRepository | > 100K msgs/s |
| `SerializationBenchmarks` | JSON serialize/deserialize with System.Text.Json | < 1 μs |
| `JsonVsMessagePackBenchmarks` | JSON vs MessagePack serialize/deserialize/on-wire size, same object graph, 100 B – 100 KB | MessagePack ~2-5x fewer allocations |

## OrderedConsume Benchmark (R8.15)

`OrderedConsumeBenchmarks` measures the per-key ordered consume path introduced by ADR-026 (R8.15).

### Goals

- Allocation ceiling: `< 512 B/op` on the `OrderedBy_On` path (ADR-003).
- Per-lane overhead is CONSTANT — not per-message: the `Allocated/op` column must be flat across
  `MessageCount` values for a fixed `LaneCount`. A rising value as N grows indicates a per-message
  allocation regression (violates ADR-003 zero-copy intent).
- The N×L `[Params]` sweep (`MessageCount ∈ {500, 2000}` × `LaneCount ∈ {1, 4, 8}`) makes this
  claim derivable from the output table without a separate slope test.

### Running

```bash
# Smoke-check registration (no full run)
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --list flat

# Short run (faster, wider CI)
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*OrderedConsume*' --job short

# Full run
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*OrderedConsume*'
```

### Throughput-floor (SAC vs consistent-hash) — DEFERRED to R8.16

The X% throughput advantage and K minimum absolute throughput for `OrderedBy` vs baseline are
**deferred** to R8.16 (ADR-026 §8). No acceptance threshold is defined in R8.15. See the
skeleton in `OrderedConsumeBenchmarks.cs` for the deferred placeholder.

## Cross-transport header mapping (R7.2)

`CrossTransportHeaderMappingBenchmarks` mierzy narzut adaptera BareWire wyłącznie na
deterministycznej powierzchni `*HeaderMapper.MapOutbound` dla wszystkich pięciu transportów
(zero I/O — brak połączeń z brokerami).

### Uruchomienie

```bash
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- \
  --filter '*CrossTransportHeaderMapping*' --exporters json csv markdown
```

Raport ląduje w `BenchmarkDotNet.Artifacts/results/*CrossTransportHeaderMapping*.md`.

### Metryki

- **Metryka główna: `Allocated` (B/op)** — mierzona przez `[MemoryDiagnoser]`. Bezpośredni
  wskaźnik presji alokacyjnej adaptera per wiadomość.
- **`Mean` (ns/op)** pełni rolę proxy throughput (R-2) — nie wymaga osobnej kolumny.

### Czytanie tabeli Ratio

`MapOutbound_RabbitMq` jest `[Benchmark(Baseline = true)]` w kategorii `CrossTransport`.
RabbitMQ alokuje krotkę + dwie kolekcje (`BasicProperties` + `Dictionary`), więc
`Ratio < 1.0` dla lżejszych transportów odzwierciedla różnice modelu obiektowego SDK —
**nie** nieefektywność adaptera BareWire (R-1). Kolumna `Allocated` jest metryką
nadrzędną nad `Ratio`.

### Azure Service Bus — osobna kategoria (D-2)

`MapOutbound_AzureServiceBus` jest w kategorii `AsbMutateInPlace` i **nie pojawia się
w tabeli Ratio**. Metoda jest `static void` i mutuje `ServiceBusMessage.ApplicationProperties`
in-place — po pierwszej iteracji klucze już istnieją w słowniku, więc alokacja w
steady-state dąży do zera (brak resize / brak alokacji węzłów). Taki profil jest
alokacyjnie nieporównywalny z transportami zwracającymi świeży obiekt per wywołanie.
Wyniki ASB są poprawne i użyteczne jako pomiar steady-state mutacji in-place, ale
nie należy ich zestawiać z kolumną `Ratio` grupy `CrossTransport` (D-2/R-3).

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

## DynamicMethod vs System.Text.Json — spike badawczy (R7.5)

`DynamicMethodVsSystemTextJsonBenchmarks` to **prototyp badawczy** (spike) porównujący
serializer JSON oparty na `Reflection.Emit.DynamicMethod` (z cache delegatów per-typ
w `ConcurrentDictionary<Type, Delegate>`) ze ścieżkami `System.Text.Json`:
**reflection** (`Serialize_StjReflection`, `Baseline = true`) oraz **source-gen**
(`Serialize_StjSourceGen`, przez `BenchmarkJsonContext`). Trzecia metoda
`Serialize_DynamicMethod` mierzy prototyp. Parametryzacja `PayloadSizeBytes` ∈
{100, 1 000, 10 000, 100 000}, ten sam graf obiektów (`BenchmarkOrder`) co
`SerializationBenchmarks`.

Kod prototypu (`Prototype/DynamicMethodJsonSerializerPrototype.cs`,
`Prototype/BenchmarkJsonContext.cs`) jest **wyłącznie w projekcie benchmarkowym** —
NIE w `src/`. To celowo wąski prototyp (`string`/`int`/`decimal`/zagnieżdżona `List<T>`),
nie produkcyjny `IMessageSerializer`. Poprawność emitowanych bajtów (parytet bajt-w-bajt
ze STJ przy `BareWireJsonSerializerOptions.Default`: camelCase + `WhenWritingNull` + Web)
jest pilnowana przez 4 testy jednostkowe w
`tests/BareWire.UnitTests/Serialization/Prototype/`.

### Uruchomienie

```bash
# Pełny przebieg (statystycznie wiarygodny — kilka minut)
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*DynamicMethodVs*'

# Eksport tabeli wyników
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- \
  --filter '*DynamicMethodVs*' --exporters json csv markdown
```

> **Uwaga o uczciwości pomiaru (D-5, PERF-1):** prototyp replikuje pooling
> `[ThreadStatic] Utf8JsonWriter` + `Reset(output)` ze `SystemTextJsonSerializer`, więc
> mierzony jest **narzut emitu/dispatchu**, a nie jednorazowa alokacja `Utf8JsonWriter`
> (~448 B). Cache delegatu jest pre-warmowany w `[GlobalSetup]` (PERF-2), aby pierwsza
> mierzona iteracja nie absorbowała kosztu emitu.

### Wyniki (status pomiaru)

Prototyp **buduje się i działa** (Release green; harness wykonuje wszystkie 12 przypadków
3 metody × 4 rozmiary). Wstępny przebieg orientacyjny (krótki, statystycznie nieostateczny —
CI bardzo szerokie) pokazuje **rzędy wielkości**: wszystkie trzy ścieżki mieszczą się w
zakresie jednocyfrowych µs dla małych payloadów, a `Serialize_DynamicMethod` **nie**
wykazuje decydującej przewagi nad `Serialize_StjSourceGen` — na najmniejszych payloadach
(100 B – 1 KB) per-call dispatch emitowanego delegatu bywa wręcz wolniejszy niż
zoptymalizowany kod source-gen STJ. Pełną, statystycznie wiarygodną tabelę B/op + Mean
należy wygenerować poleceniem powyżej na docelowym hoście CI/dev i wkleić tutaj
(kolumny: `Method`, `PayloadSizeBytes`, `Mean`, `Allocated`, `Ratio`, `Alloc Ratio`).

Próg „obiecujące" (D-3): GO wymagałby ≥30% mniej B/op LUB ≥20% wyższego throughput vs
**source-gen** na 100 B i 1 KB, przy regresji ≤5% na 10 KB/100 KB. Przebieg orientacyjny
tego progu nie osiąga.

### Werdykt: **NO-GO** (dla domyślnego serializera produkcyjnego)

Rekomendacja jest **NO-GO** dla zastąpienia/uzupełnienia domyślnego serializera ścieżką
DynamicMethod, z trzech powodów — z których **dwa są niezależne od wyników wydajności**:

1. **AOT / trim (D-4, twarda bramka).** `DynamicMethod` jest JIT-only — z założenia
   niekompatybilny z Native AOT i agresywnym trimmingiem. Uczynienie go domyślnym
   serializerem zamknęłoby konsumentom BareWire drogę do AOT. To architektoniczny
   argument NO-GO **niezależny od liczb**. STJ source-gen jest AOT-safe i już dostępny.
2. **Brak decydującej przewagi wydajnościowej.** Właściwym konkurentem jest STJ
   **source-gen** (nie reflection), który również usuwa refleksję w runtime. Prototyp
   nie pokazuje przewagi spełniającej próg D-3; na małych payloadach bywa wolniejszy.
   Złożoność utrzymania emitera IL nie jest uzasadniona marginalnym (lub ujemnym) zyskiem.
3. **Granica enkapsulacji i zakres serialize-only (D-6).** Produkcyjny emiter wymagałby
   `DynamicMethod(skipVisibility: true)`, omijając dostępność składowych — nowy wzorzec
   codegen w repo, argument za source-gen. Ponadto werdykt dotyczy **wyłącznie ścieżki
   serialize**; produkcyjna ścieżka deserialize emit niesie ryzyko untrusted-input/
   type-confusion i musiałaby zachować kontrakt `TryDeserialize<T>()` null-on-malformed
   (security-architecture.md §4.3) — ten koszt nie został zmierzony i powiększa NO-GO.

**Rekomendacja kierunkowa:** jeśli kiedykolwiek potrzebny będzie szybszy/mniej alokujący
JSON niż STJ-reflection, właściwą drogą jest **STJ source-gen** (`JsonSerializerContext`),
nie emiter DynamicMethod — daje większość zysku przy zachowaniu AOT-safety i bez własnego
kodu IL. Prototyp i benchmark pozostają w repo jako udokumentowany dowód spike'u (przyszli
czytelnicy nie powtarzają badania). Produkcyjna implementacja `IMessageSerializer`
(D-PROD) NIE jest rekomendowana — osobne zadanie roadmap nie jest potrzebne.

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
