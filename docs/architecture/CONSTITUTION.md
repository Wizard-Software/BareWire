# CONSTITUTION.md — Prawo kodeksu BareWire

> Ten dokument jest **pierwszym**, który agent AI musi przeczytać przed jakąkolwiek pracą z kodem BareWire.
> Definiuje konwencje nazewnicze, reguły granic, standardy kodowania i ADR enforcement.

---

## 1. Konwencje nazewnicze

### Projekty i namespace

| Element | Konwencja | Przykład |
|---------|-----------|---------|
| Namespace root | `BareWire` | `BareWire.Abstractions`, `BareWire.Core` |
| Projekt src | `BareWire.{Component}` | `BareWire.Transport.RabbitMQ` |
| Projekt test | `BareWire.{TestType}` | `BareWire.UnitTests`, `BareWire.IntegrationTests` |
| Klasa testowa | `{KlasaTestowana}Tests` | `FlowControllerTests` |
| Metoda testowa | `{Metoda}_{Scenariusz}_{Wynik}` | `TryGrantCredits_WhenAtLimit_ReturnsFalse` |

### Interfejsy i klasy

| Element | Konwencja | Przykład |
|---------|-----------|---------|
| Publiczny interfejs | `I{Nazwa}` | `IBus`, `IConsumer<T>`, `ITransportAdapter` |
| Implementacja wewnętrzna | `{Nazwa}` (internal class) | `BareWireBus`, `MessagePipeline` |
| Abstrakcyjna klasa bazowa | `BareWire{Nazwa}` lub `{Nazwa}Base` | `BareWireStateMachine<T>` |
| Opcje konfiguracji | `{Komponent}Options` | `FlowControlOptions`, `RawSerializerOptions` |
| Middleware | `{Nazwa}Middleware` | `RetryMiddleware`, `InMemoryOutboxMiddleware` |
| Exception | `BareWire{Nazwa}Exception` | `BareWireConfigurationException` |

### Metody async

| Element | Konwencja |
|---------|-----------|
| Publiczne async | Sufiks `Async` — `PublishAsync`, `ConsumeAsync` |
| `CancellationToken` | Ostatni parametr, domyślnie `default` |
| Return type | `Task` lub `ValueTask` (nigdy `async void`) |
| ConfigureAwait | `ConfigureAwait(false)` w kodzie biblioteki |

### Wiadomości

| Element | Konwencja | Przykład |
|---------|-----------|---------|
| Message type | `record` (immutable) | `public record OrderCreated(string OrderId, decimal Amount, string Currency)` |
| Brak klasy bazowej | Wiadomości to POCO/record — brak `IMessage`, brak atrybutów | — |
| Nazewnictwo | Przeszły czas dla eventów, imperatyw dla komend | `OrderCreated`, `RequestPayment` |

---

## 2. Reguły granic (Boundary Rules)

### ZAWSZE ROBIMY (ALWAYS DO)

- [ ] `ConfigureConsumeTopology = false` domyślnie — ręczna topologia
- [ ] Raw JSON jako domyślny format — brak koperty
- [ ] `CancellationToken` propagowany w każdej metodzie async
- [ ] Nullable Reference Types enabled (`<Nullable>enable</Nullable>`)
- [ ] `ArgumentNullException` dla parametrów nie-nullable
- [ ] Bufory z `ArrayPool<byte>.Shared` — nigdy `new byte[]` w hot path
- [ ] `IAsyncDisposable` na komponentach z zasobami (connections, channels)
- [ ] Structured logging z `ILogger<T>` — MessageId i CorrelationId w każdym logu
- [ ] `internal` visibility dla klas implementacyjnych — tylko interfejsy publiczne (wyjątek: `SagaDbContext` musi być `public` non-sealed — EF Core wymaga tego do migracji i `OnModelCreating`)
- [ ] SemVer strict — breaking changes tylko w major versions

### ZAPYTAJ NAJPIERW (ASK FIRST)

- [ ] Dodanie nowego publicznego interfejsu — wymaga review `.approved.txt`
- [ ] Zmiana sygnatury publicznej metody — potencjalny breaking change
- [ ] Dodanie dependency na zewnętrzny pakiet NuGet — wpływa na dependency tree
- [ ] Zmiana domyślnych wartości opcji (PrefetchCount, MaxInFlight, etc.)
- [ ] Dodanie auto-topologii — musi być opt-in, nigdy domyślne

### NIGDY NIE ROBIMY (NEVER DO)

- [ ] Alokacja `byte[]` per-message w hot path — ZAWSZE `ArrayPool`
- [ ] Unbounded channels/buffers — ZAWSZE bounded z konfigurowalnymi limitami
- [ ] Static mutable state — ZAWSZE DI Singleton/Scoped
- [ ] Envelope jako domyślny format — ZAWSZE raw-first
- [ ] Auto-topologia jako default — ZAWSZE `ConfigureConsumeTopology = false`
- [ ] Dependency na MassTransit — NIGDY (tylko konwencje nazewnicze)
- [ ] `async void` — NIGDY
- [ ] Catch-all `catch (Exception)` bez re-throw lub explicit handling
- [ ] Logowanie sekretów (hasła, tokeny, certyfikaty) — NIGDY

---

## 3. Standardy kodowania

### C# 14 / .NET 10

- Używaj `extension members` gdzie to poprawia ergonomię API
- Używaj `field` keyword w auto-properties (eliminacja `_backing` fields)
- Używaj `null-conditional assignment` (`x ??= value`)
- Używaj `implicit span conversions` w hot path
- Preferuj `record` nad `class` dla immutable data types
- Preferuj `sealed` class chyba że dziedziczenie jest zamierzone
- Preferuj `ReadOnlySpan<T>` i `ReadOnlyMemory<T>` nad `T[]` w publicznym API

### Formatting

- Indentation: 4 spacje (bez tabów)
- Nawiasy klamrowe: Allman style (nowa linia)
- Max line length: 120 znaków
- `using` directives: na szczycie pliku, posortowane (System first)
- File-scoped namespaces (`namespace BareWire.Core;`)

### Dokumentacja XML

- Wszystkie publiczne typy i metody mają `<summary>`
- Parametry mają `<param>`
- Wyjątki mają `<exception cref="...">`
- Brak dokumentacji dla `internal` — komentarze tylko gdy logika nie jest oczywista

---

## 4. Podsumowanie ADR

| ADR | Decyzja | Enforcement |
|-----|---------|-------------|
| [ADR-001](decisions/ADR-001-raw-first-no-envelope.md) | Raw-first (brak koperty domyślnie) | Domyślny serializer produkuje raw JSON; envelope wymaga jawnego opt-in |
| [ADR-002](decisions/ADR-002-manual-topology-default.md) | Ręczna topologia jako default | `ConfigureConsumeTopology = false` domyślnie; auto-topologia = opt-in |
| [ADR-003](decisions/ADR-003-zero-copy-pipeline.md) | Zero-copy pipeline (Pipelines + pooling) | `IBufferWriter<byte>` / `ReadOnlySequence<byte>`; `ArrayPool` only |
| [ADR-004](decisions/ADR-004-credit-based-flow-control.md) | Credit-based flow control | Bounded channels; atomic inflight tracking; health alerts at 90% |
| [ADR-005](decisions/ADR-005-masstransit-naming-conventions.md) | Nazwy wzorowane na MassTransit | IBus, IConsumer<T>, ConsumeContext<T> w namespace BareWire.Abstractions |
| [ADR-006](decisions/ADR-006-publish-backpressure.md) | Publish-side backpressure | Bounded outgoing channel; `PublishFlowControlOptions`; health alerts at 90% |

---

## 5. Struktura solution

```
BareWire.slnx
├── src/
│   ├── BareWire.Abstractions/        ← interfejsy, zero deps
│   ├── BareWire.Core/                ← pipeline, flow control, retry
│   ├── BareWire.Serialization.Json/  ← System.Text.Json zero-copy
│   ├── BareWire.Transport.RabbitMQ/  ← adapter RabbitMQ
│   ├── BareWire.Saga/                ← SAGA engine
│   ├── BareWire.Saga.EntityFramework/← persystencja SAGA EF Core 10
│   ├── BareWire.Outbox/              ← outbox/inbox engine
│   ├── BareWire.Outbox.EntityFramework/← transactional outbox EF Core 10
│   ├── BareWire.Observability/       ← OTel, EventCounters, HealthChecks
│   └── BareWire.Testing/            ← InMemoryTransport, TestHarness
├── tests/
│   ├── BareWire.AppHost/             ← Aspire AppHost (infrastruktura testowa)
│   ├── BareWire.UnitTests/
│   ├── BareWire.IntegrationTests/    ← Aspire + RabbitMQ
│   ├── BareWire.ContractTests/       ← PublicApiGenerator, NetArchTest
│   └── BareWire.Benchmarks/          ← BenchmarkDotNet
├── samples/
│   ├── BareWire.Samples.AppHost/          ← Aspire AppHost (orkiestracja sample'ów)
│   ├── BareWire.Samples.ServiceDefaults/  ← OTel + health checks defaults
│   ├── BareWire.Samples.RabbitMQ/         ← podstawowy sample (publish/consume/saga/outbox)
│   ├── BareWire.Samples.BasicPublishConsume/ ← prosty publish/consume + PostgreSQL
│   ├── BareWire.Samples.RequestResponse/  ← request/response pattern
│   ├── BareWire.Samples.RawMessageInterop/ ← interop z raw messages
│   ├── BareWire.Samples.SagaOrderFlow/    ← SAGA + RoutingSlip + timeout + compensation
│   ├── BareWire.Samples.TransactionalOutbox/ ← outbox/inbox exactly-once delivery
│   ├── BareWire.Samples.RetryAndDlq/     ← retry + RabbitMQ native DLX
│   ├── BareWire.Samples.BackpressureDemo/ ← ADR-004/006 flow control
│   ├── BareWire.Samples.ObservabilityShowcase/ ← pełny stos OTel
│   └── BareWire.Samples.MultiConsumerPartitioning/ ← topic routing + partitioner
├── Directory.Build.props
├── Directory.Packages.props          ← Central Package Management
└── .editorconfig
```

---

## 6. Pakiety i ich granice

| Pakiet | Zależy od | NIE może zależeć od |
|--------|-----------|---------------------|
| BareWire.Abstractions | (zero deps) | Core, Transport, Observability, Saga, Outbox |
| BareWire.Core | Abstractions | Transport, Observability |
| BareWire.Serialization.Json | Abstractions | Core, Transport, Observability |
| BareWire.Transport.RabbitMQ | Abstractions | Core, Observability |
| BareWire.Saga | Abstractions, Core | Transport, Observability |
| BareWire.Saga.EntityFramework | Abstractions, Saga | Transport, Observability |
| BareWire.Outbox | Abstractions, Core | Transport, Observability |
| BareWire.Outbox.EntityFramework | Abstractions, Outbox | Transport, Observability |
| BareWire.Observability | Abstractions | Core, Transport |
| BareWire.Testing | Abstractions, Core | Transport (produkcyjny) |

**Egzekwowanie:** NetArchTest w `BareWire.ContractTests` weryfikuje te reguły per CI build.

---

*Ten dokument jest generowany automatycznie i powinien być aktualizowany przy każdej zmianie architektonicznej.*
