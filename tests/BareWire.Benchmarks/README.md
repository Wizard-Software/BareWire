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
| `ConsumerEnvelopeDispatchBenchmarks` | Per-consumer MassTransit-envelope deserializer selection (`ReceiveEndpointRunner.ResolverFor`); no-opt-in degradation to the pre-18.5 dispatch path (18.5, ADR-031 D4) | 0 B/op (no-opt-in) |
| `ConsumerDefinitionDispatchAllocationBenchmarks` | Consumer-definition dispatch WITHOUT any definition opt-in — discovery + `TMessage` inference baked ONCE at start-up (19.6 seam), per-delivery read of baked delegate + precompiled `ConsumerRegistration` fields (19.12) | 0 B/op (no-opt-in) |

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

### Throughput-floor (SAC vs consistent-hash) — zmierzone w R8.16

Pomiar trzech ścieżek RabbitMQ (competing-consumers, SAC, consistent-hash) zrealizowany
przez harness integracyjny R8.16. Wyniki poniżej.

#### Wyniki pomiaru (środowisko: lokalny Docker, RabbitMQ 4.x via Aspire, .NET 10, 2026-06-25)

| Ścieżka | Mediana [msgs/s] | Uwagi |
|---------|-----------------|-------|
| Competing-consumers (4 konsumentów, 1 kolejka) | ≈ 1 846 | baseline (mianownik) |
| SAC (4 podłączonych, 1 aktywny) | ≈ 1 950 | floor = 1 konsument; broker-bottlenecked ≈ competing |
| Consistent-hash (K=16 kolejek, 4 konsumentów) | ≈ 1 792 (P20) | 97.1% baseline; próg= 87% |

**Wyznaczone stałe (ADR-026 §8):**
- **K = 16** — liczba per-key queues bound do consistent-hash exchange
- **X% = 87%** — minimalny próg stosunku consistent-hash/competing-consumers (p20 − 10 p.p. margines)

**Interpretacja:**
- **SAC** = „uporządkowane, zero równoległości w kolejce" — w środowisku Docker-bottlenecked osiąga
  przepustowość podobną do competing-consumers (broker/sieć, nie ConsumerInstances, jest wąskim gardłem
  przy 5 000 msgs). Jest to poprawne zachowanie: SAC floor = przepustowość 1 aktywnego konsumenta,
  ale gdy broker bottleneck dominuje, 4 consumers nie dają 4× throughput.
- **Consistent-hash + per-key queues** = równoległość po kluczach, afinicja klucz→kolejka,
  okno re-mapy przy zmianie liczby kolejek/restart węzła. P20 = 97.1% competing-consumers
  przy K=16 (plateau; sweep K∈{8,16,32} pokazał brak istotnych różnic między K).
- **Re-map window**: przy zmianie liczby bound queues (lub restarcie węzła) consistent-hash
  re-mapy klucze; przez okno re-mapu możliwe chwilowe złamanie afinicji per-klucz.

**Uruchomienie harnessu RabbitMQ:**
```bash
dotnet test tests/BareWire.IntegrationTests/ --filter "Category=Throughput"
```

**Sweep exploracyjny K∈{8,16,32}:**
```bash
dotnet test tests/BareWire.IntegrationTests/ --filter "Category=ThroughputSweep"
```

`OrderedConsumeBenchmarks` (BenchmarkDotNet, ten plik) mierzy narzut alokacyjny warstwy
ordered-dispatch (local ordered lanes vs sequential baseline, in-memory, bez brokera) —
NIE jest pomiarem floor RabbitMQ. Pomiar RabbitMQ żyje w
`tests/BareWire.IntegrationTests/Transport/RabbitMqThroughputFloorTests.cs`.

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

## ConsumerEnvelopeDispatch Benchmark (18.8)

`ConsumerEnvelopeDispatchBenchmarks` measures the per-consumer MassTransit-envelope deserializer
selection introduced by task 18.5 (ADR-031 D4 precedence): `ReceiveEndpointRunner.ResolverFor(int)`.

### Goal

- **0 B/op on the no-opt-in path.** When NO consumer on an endpoint opts into `UseMassTransitEnvelope()`
  (the default for ~all deployments), the per-delivery selection arithmetic must degrade to the exact
  pre-18.5 behaviour: `_hasAnyMtEnvelope == false` short-circuits the `&&` and returns the
  reference-identical `_deserializerResolver`, allocating nothing. The opt-in must not tax the path that
  does not use it.
- The benchmark mirrors the production `ResolverFor` ternary one-to-one and reaches the real internal
  `SingleDeserializerResolver` seam via `[InternalsVisibleTo("BareWire.Benchmarks")]` — no production
  code or public API is touched.

### Two measured paths

- `Select_NoOptIn` — `_hasAnyMtEnvelope == false`: the short-circuit returning `_deserializerResolver`.
  **Target: `0 B/op` (the gate).**
- `Select_AllOptIn` — every consumer opts in: returns `_mtResolver`. Documents that the selection
  arithmetic itself is always allocation-free; the opt-in cost lives downstream in deserialization, not
  in selection. Expected `0 B/op`.

The consumer invocation itself (scope creation + payload deserialization) is **out of scope** — it
allocates by definition and is not part of the selection cost the gate governs (same boundary as
`DispatchBenchmarks`).

### Running

```bash
# Smoke-check registration (no full run)
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --list flat | grep ConsumerEnvelope

# Short run (allocation is deterministic — a short job is a reliable B/op measurement)
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*ConsumerEnvelopeDispatch*' --job short

# Full run
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*ConsumerEnvelopeDispatch*'
```

The `Allocated` column must read `-` / `0 B` for both methods.

## ConsumerDefinitionDispatch Allocation Benchmark (19.12)

`ConsumerDefinitionDispatchAllocationBenchmarks` proves that the consume-time dispatch path WHEN NO
consumer definition opts in stays **0-B/op**, and that the global `< 512 B/op` consume budget is
unchanged by the consumer-definition enhancement (19.x).

### Goal

- **Discovery + `TMessage` inference happen ONCE at start-up, not per delivery.** The closed invoker
  delegate is baked in `[GlobalSetup]` via the production `ConsumerInvokerFactory.Create` seam (the 19.6
  `MakeGenericMethod` path). That reflection is a start-up cost — it never runs per delivery.
- **0 B/op on the no-opt-in path.** The measured method `Dispatch_NoDefinitionOptIn_SettingsRead` does
  only what the dispatcher does per delivery on the default-off path: read the already-baked delegate
  reference and read the precompiled `ConsumerRegistration` fields (all opt-in knobs default-off). No
  `MakeGenericMethod`, no reflection, no per-message lookup, no allocation. **Target: `0 B/op` (the gate).**
- **Settings read is precompiled**, not a per-message lookup — the definition settings live as
  `ConsumerRegistration` fields read directly.
- The actual invocation of the baked delegate is OUT of the measured path (invoking a consumer allocates a
  DI scope + deserialization) — same boundary as `DispatchBenchmarks` and `ConsumerEnvelopeDispatchBenchmarks`.
- The global `< 512 B/op` consume budget stays guarded by `ConsumeBenchmarks.ConsumeAndAck_InMemory`
  (transport floor, unchanged by the enhancement).

### Running

```bash
# Smoke-check registration (no full run)
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --list flat | grep ConsumerDefinitionDispatch

# Short run (allocation is deterministic — a short job is a reliable B/op measurement)
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*ConsumerDefinitionDispatch*' --job short

# Full run
dotnet run --project tests/BareWire.Benchmarks/ -c Release -- --filter '*ConsumerDefinitionDispatch*'
```

The `Allocated` column must read `-` / `0 B` for `Dispatch_NoDefinitionOptIn_SettingsRead`.

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
- Pomiar przepustowości na realnym brokerze RabbitMQ żyje w harnessie integracyjnym
  (`RabbitMqThroughputFloorTests`, sekcja „Throughput-floor SAC vs consistent-hash" powyżej) —
  uruchamiany lokalnie/nightly przez Aspire, NIE w BenchmarkDotNet. Benchmarki BenchmarkDotNet
  in-memory walidują rdzeń pipeline'u bez żywego brokera.
- CI runs benchmarks with `continue-on-error: true` — results are uploaded as artifacts for manual review.
