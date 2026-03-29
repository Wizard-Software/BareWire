# BareWire — Zadania implementacyjne MVP

| Pole | Wartość |
|------|---------|
| Wygenerowano | 2026-03-14 |
| Źródło TDD | [docs/TDD.md](../TDD.md) |
| Architektura | solution-design (`docs/architecture/`) |
| Stos technologiczny | .NET 10 / C# 14, RabbitMQ (MVP), EF Core 10, System.Text.Json, OpenTelemetry |
| Styl architektury | Layered Library (4 warstwy, NuGet per komponent) |

## Legenda

- `N.M.` — identyfikator zadania (N = numer funkcjonalności, M = numer zadania)
- `[ ]` — nie rozpoczęte
- `[x]` — ukończone
- `[unit]` — wymaga testu jednostkowego
- `[integration]` — wymaga testu integracyjnego
- `[e2e]` — wymaga testu end-to-end
- `[no-test]` — test nie jest wymagany
- `->` — referencja do dokumentu architektury

---

## Podsumowanie

| Funkcjonalność | Zadania | Unit | Integration | E2E | No-test |
|----------------|---------|------|-------------|-----|---------|
| 0. Bootstrap | 8 | 0 | 0 | 0 | 8 |
| 1. Fundament (Abstractions + Core + Testing) | 21 | 14 | 3 | 0 | 4 |
| 2. Serializacja | 6 | 5 | 0 | 0 | 1 |
| 3. Transport RabbitMQ | 10 | 2 | 7 | 1 | 0 |
| 4. SAGA Engine | 12 | 8 | 2 | 1 | 1 |
| 5. Outbox / Inbox | 9 | 4 | 2 | 1 | 2 |
| 6. Observability | 7 | 6 | 1 | 0 | 0 |
| 7. Kontrakty + Benchmarki + Samples | 8 | 2 | 0 | 1 | 5 |
| 8. Samples — scenariusze użycia | 11 | 0 | 0 | 0 | 11 |
| 9. Refaktoryzacja | 1 | 0 | 0 | 0 | 1 |
| 10. Bugi wykryte w E2E testach samples | 18 | 1 | 2 | 1 | 14 |
| 11. Ulepszenia architektury (code review) | 8 | 5 | 0 | 0 | 3 |
| 12. Interop MassTransit | 7 | 4 | 0 | 0 | 3 |
| **Razem** | **125** | **51** | **17** | **4** | **53** |

---

## Funkcjonalność 0: Bootstrap

> Scaffolding projektu, środowisko deweloperskie i współdzielona infrastruktura.
> Ta funkcjonalność musi być ukończona przed wszystkimi pozostałymi.

### Struktura projektu

- [x] **0.1. Utwórz solution z warstwową strukturą projektów** `[no-test]`
  BareWire.sln z wszystkimi projektami src/ i tests/. Directory.Build.props ze wspólnymi ustawieniami (.NET 10, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, file-scoped namespaces).
  -> [CONSTITUTION.md](../architecture/CONSTITUTION.md)

- [x] **0.2. Skonfiguruj Central Package Management (CPM) i .editorconfig** `[no-test]`
  Directory.Packages.props z wersjami: xunit.v3 3.2.x, AwesomeAssertions 9.x, NSubstitute 5.3.x, BenchmarkDotNet 0.15.x, System.Text.Json, RabbitMQ.Client 7.x, EF Core 10, OpenTelemetry SDK. .editorconfig: 4 spacje, Allman braces, 120 znaków.
  -> [CONSTITUTION.md](../architecture/CONSTITUTION.md)

- [x] **0.3. Skonfiguruj referencje projektów i granice zależności** `[no-test]`
  `<ProjectReference>` zgodnie z regułami warstw: Transport i Observability zależą tylko od Abstractions; Core zależy od Abstractions; Saga i Outbox zależą od Abstractions + Core.
  -> [CONSTITUTION.md](../architecture/CONSTITUTION.md)

### Środowisko deweloperskie

- [x] **0.4. Utwórz Aspire AppHost dla testów integracyjnych** `[no-test]`
  Aspire hosting z RabbitMQ resource (kontener Docker). Connection string discovery dla projektów testowych. Health check na gotowość brokera.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

### CI/CD

- [x] **0.5. Skonfiguruj GitHub Actions CI pipeline** `[no-test]`
  Kroki: restore, build, test (unit + integration), publish test results. Trigger na PR do main. Cache NuGet packages.
  -> [implementation-plan.md](../architecture/implementation-plan.md)

### Infrastruktura testowa

- [x] **0.6. Utwórz projekt testów jednostkowych z infrastrukturą** `[no-test]`
  BareWire.UnitTests z xunit.v3, FluentAssertions, NSubstitute. Struktura katalogów: Core/, Serialization/, Saga/, Outbox/, Transport/.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **0.7. Utwórz projekt testów integracyjnych z AspireFixture** `[no-test]`
  BareWire.IntegrationTests z AspireFixture do zarządzania kontenerami RabbitMQ. Shared `IClassFixture<AspireFixture>`.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **0.8. Utwórz projekt benchmarków z BenchmarkDotNet** `[no-test]`
  BareWire.Benchmarks z `[MemoryDiagnoser]`, `[EventPipeProfiler(GcVerbose)]`. Struktura: PublishBenchmarks, ConsumeBenchmarks, SerializationBenchmarks.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

---

## Funkcjonalność 1: Fundament (Abstractions + Core + Testing)

> Faza 1 z planu implementacji. Kompilujący szkielet z interfejsami publicznymi,
> pipeline przetwarzania, flow control i InMemoryTransport.
> Kryterium zakończenia: `dotnet test` przechodzi; publish/consume przez InMemoryTransport działa.

### BareWire.Abstractions

- [x] **1.1. Utwórz interfejsy rdzenne szyny i konsumentów** `[no-test]`
  `IBus`, `IBusControl`, `IPublishEndpoint`, `ISendEndpoint`, `ISendEndpointProvider`, `IConsumer<T>`, `IRawConsumer`, `IRequestClient<T>`, `Response<T>`. Wszystkie z XML docs. `IBus` dziedziczy `IPublishEndpoint`, `ISendEndpointProvider`, `IDisposable`, `IAsyncDisposable`.
  -> [public-api.md](../architecture/api/public-api.md)

- [x] **1.2. Utwórz interfejsy transportu, serializacji, middleware i konfiguracji endpointu** `[no-test]`
  `ITransportAdapter` (SendBatchAsync, ConsumeAsync jako IAsyncEnumerable, SettleAsync, DeployTopologyAsync), `ITopologyConfigurator`, `IHeaderMappingConfigurator`, `IMessageSerializer`, `IMessageDeserializer`, `IMessageMiddleware`, `IReceiveEndpointConfigurator` z pełną konfiguracją per endpoint.
  -> [public-api.md](../architecture/api/public-api.md), [extension-points.md](../architecture/architecture/extension-points.md)

- [x] **1.3. Utwórz abstrakcje SAGA** `[no-test]`
  `ISagaState` (CorrelationId, CurrentState, Version), `ISagaRepository<T>` (Find/Save/Update/Delete), `IQueryableSagaRepository<T>`, `BareWireStateMachine<T>` z fluent API (Event, State, During, Initially, CorrelateBy, Schedule), `IEventActivityBuilder<TSaga, TEvent>`, `IScheduleConfigurator`, `ICompensableActivity<TArgs, TLog>`.
  -> [public-api.md](../architecture/api/public-api.md), [TDD.md sekcja 11](../TDD.md)

- [x] **1.4. Utwórz typy kontekstowe** `[unit]`
  `ConsumeContext` (abstract): MessageId, CorrelationId, ConversationId, Headers, RawBody, CancellationToken, RespondAsync, PublishAsync, GetSendEndpoint. `ConsumeContext<T>` z Message. `RawConsumeContext` z `TryDeserialize<T>()`. Testy: tworzenie kontekstu, dostęp do properties.
  -> [public-api.md](../architecture/api/public-api.md)

- [x] **1.5. Utwórz typy konfiguracyjne, enumeracje i hierarchię wyjątków** `[unit]`
  `FlowControlOptions` (MaxInFlightMessages, MaxInFlightBytes, InternalQueueCapacity, FullMode), `PublishFlowControlOptions` (ADR-006), `RawSerializerOptions` [Flags], `TransportCapabilities` [Flags], `SettlementAction`, `ExchangeType`, `SchedulingStrategy`. Hierarchia wyjątków: `BareWireException` → `BareWireConfigurationException`, `BareWireTransportException` → `RequestTimeoutException`, `TopologyDeploymentException`, `BareWireSerializationException`, `BareWireSagaException` → `ConcurrencyException`, `UnknownPayloadException`. Testy: tworzenie, wartości domyślne, messages.
  -> [public-api.md](../architecture/api/public-api.md), [error-handling.md](../architecture/api/error-handling.md)

### BareWire — Bufory i Flow Control

- [x] **1.6. Zaimplementuj MessageRingBuffer<T> i PooledBufferWriter** `[unit]`
  `MessageRingBuffer<T>`: SPSC, power-of-2 capacity, `Volatile.Read/Write`, `TryWrite`/`TryRead`, zero-alloc. `PooledBufferWriter`: `IBufferWriter<byte>` z `ArrayPool<byte>.Shared`, `GetMemory`/`GetSpan`/`Advance`, `Dispose` zwraca bufor. Testy: write/read, pełny/pusty bufor, resize, pool return.
  -> [internal-components.md](../architecture/architecture/internal-components.md), [TDD.md sekcja 10](../TDD.md)

- [x] **1.7. Zaimplementuj FlowController, CreditManager i EndpointPipeline** `[unit]`
  `FlowController`: zarządzanie kredytami per endpoint. `CreditManager`: atomowe `TryGrantCredits`, `TrackInflight`/`ReleaseInflight` (Interlocked). `EndpointPipeline`: `Channel.CreateBounded<InboundMessage>`, inflight tracking (`_inflightCount`, `_inflightBytes`), health alert at 90%. Testy: grant/deny credits, limits, bounded channel backpressure.
  -> [internal-components.md](../architecture/architecture/internal-components.md), [ADR-004](../architecture/decisions/ADR-004-credit-based-flow-control.md)

### BareWire — Pipeline

- [x] **1.8. Zaimplementuj MiddlewareChain i ConsumerDispatcher** `[unit]`
  `MiddlewareChain`: łańcuch `IMessageMiddleware` w kolejności rejestracji (FIFO). Wbudowane pozycje: custom global → Retry → Outbox → Partitioner → custom endpoint → dispatch. `ConsumerDispatcher`: dispatch do `IConsumer<T>` z DI scope (`IServiceScopeFactory`). Testy: kolejność middleware, propagacja CancellationToken, wyjątek w middleware.
  -> [internal-components.md](../architecture/architecture/internal-components.md), [extension-points.md](../architecture/architecture/extension-points.md)

- [x] **1.9. Zaimplementuj MessagePipeline** `[unit]`
  Orchestracja inbound: middleware chain → deserializacja → consumer dispatch → settlement (ack/nack/DLQ). Orchestracja outbound: serialize → adapter.SendBatch. Testy: pełny flow z mock middleware i mock adapter.
  -> [internal-components.md](../architecture/architecture/internal-components.md)

- [x] **1.10. Zaimplementuj BareWireBus i BareWireBusControl** `[unit]`
  `BareWireBus`: routing `PublishAsync<T>` i `SendAsync<T>`, endpoint resolution (`GetSendEndpoint`), `CreateRequestClient<T>`. `BareWireBusControl`: `StartAsync`/`StopAsync`, `DeployTopologyAsync`, `CheckHealth()` → `BusHealthStatus`. Rejestrowany jako Singleton. Thread-safe. Testy: publish routing, lifecycle, health check.
  -> [public-api.md](../architecture/api/public-api.md), [internal-components.md](../architecture/architecture/internal-components.md)

- [x] **1.11. Zaimplementuj BareWireBusHostedService** `[integration]`
  `IHostedService`: `StartAsync` → `IBusControl.StartAsync()`, `StopAsync` → graceful drain + `IBusControl.StopAsync()`. Rejestracja w DI. Test integracyjny: host start/stop lifecycle z WebApplicationFactory.
  -> [configuration.md](../architecture/api/configuration.md)

### BareWire — Middleware

- [x] **1.12. Zaimplementuj RetryMiddleware i DeadLetterMiddleware** `[unit]`
  `RetryMiddleware`: strategie Interval, Incremental, Exponential backoff. Filtry: `Handle<TException>`, `Ignore<TException>`. Konfigurowalny max retries. `DeadLetterMiddleware`: routing do DLQ po wyczerpaniu retry. Callback `OnDeadLetter`. Testy: retry count, backoff intervals, exception filtering, DLQ routing.
  -> [error-handling.md](../architecture/api/error-handling.md), [extension-points.md](../architecture/architecture/extension-points.md)

- [x] **1.13. Zaimplementuj PartitionerMiddleware** `[unit]`
  Serializacja przetwarzania per klucz (`CorrelationId` lub custom `Func<ConsumeContext, Guid>`). `SemaphoreSlim(1, 1)` per partycja. Konfigurowalny `partitionCount`. Testy: concurrent messages z tym samym kluczem przetwarzane sekwencyjnie.
  -> [internal-components.md](../architecture/architecture/internal-components.md), [TDD.md sekcja 11.6](../TDD.md)

- [x] **1.14. Zaimplementuj publish-side backpressure (ADR-006)** `[unit]`
  Bounded outgoing channel dla `PublishAsync`. `PublishFlowControlOptions` (MaxPendingPublishes, FullMode). Health alert at 90% capacity. Testy: backpressure activation, health status degradation.
  -> [ADR-006](../architecture/decisions/ADR-006-publish-backpressure.md)

### BareWire — DI i konfiguracja

- [x] **1.15. Utwórz ServiceCollectionExtensions i walidację konfiguracji** `[integration]`
  `AddBareWire(Action<IBusConfigurator>)`: rejestracja IBus/IBusControl (Singleton), IHostedService, IPublishEndpoint, ISendEndpointProvider. Fluent API: `UseRabbitMQ`, `ConfigureObservability`, `AddMiddleware<T>`, `UseSerializer<T>`. Fail-fast validation w `StartAsync()`: URI hosta, consumer/SAGA na endpoint, PrefetchCount > 0, brak cykli topologii. Test: DI resolution, validation errors.
  -> [configuration.md](../architecture/api/configuration.md), [error-handling.md](../architecture/api/error-handling.md)

### BareWire.Testing

- [x] **1.16. Zaimplementuj InMemoryTransportAdapter** `[unit]`
  `ITransportAdapter` in-memory: `ConcurrentDictionary<string, Channel<InboundMessage>>`. `SendBatchAsync` → write to channel. `ConsumeAsync` → read from channel. `DeployTopologyAsync` → no-op. `TransportCapabilities.None`. Testy: send/consume, multiple endpoints, dispose.
  -> [internal-components.md](../architecture/architecture/internal-components.md), [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **1.17. Zaimplementuj BareWireTestHarness i MessageContextBuilder** `[unit]`
  `BareWireTestHarness`: `CreateBus(Action<IBusConfigurator>)` z InMemoryTransport. `WaitForPublish<T>(TimeSpan timeout)`, `WaitForSend<T>(TimeSpan timeout)`. `MessageContextBuilder`: fluent builder `WithMessageId`, `WithCorrelationId`, `WithPayload<T>`, `WithHeaders`, `Build()` → `ConsumeContext<T>`. Testy: wait with timeout, builder output.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

### Testy i benchmarki Fazy 1

- [x] **1.18. Dodaj testy jednostkowe komponentów Core** `[unit]`
  Pokrycie: `MessageRingBuffer` (write/read/overflow), `FlowController` (credits at limit), `EndpointPipeline` (inflight tracking), `MiddlewareChain` (ordering, exception), `PooledBufferWriter` (advance, resize, dispose). Konwencja: `{Method}_{Scenario}_{ExpectedResult}`.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **1.19. Dodaj test integracyjny: publish/consume przez InMemoryTransport** `[integration]`
  Scenariusze: typowany publish → consume → verify Message. Raw publish → consume → verify RawBody. Publish z middleware (retry). Multiple consumers na jednym endpoint.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **1.20. Dodaj benchmark: publish/consume in-memory** `[no-test]`
  `PublishBenchmarks`: throughput (msgs/s) i alokacje (B/msg) dla typowanego i raw publish. `ConsumeBenchmarks`: consume + ack throughput. Target: > 500K msgs/s publish, < 256 B/msg.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **1.21. Zintegruj FlowController z ReceiveEndpointRunner (per-message credit tracking)** `[unit]`
  `ReceiveEndpointRunner` obecnie przekazuje `PrefetchCount` do `FlowControlOptions`, ale nie integruje się z `FlowController` per-message (brak `RequestCredits`/`ReleaseInflight` per wiadomość). Zgodnie z diagramem consume flow w `internal-components.md`, adapter powinien żądać kredytów od `FlowController` przed dispatchem, a pipeline zwalniać po settlement. Wymaga: inject `FlowController` do `ReceiveEndpointRunner`, wywołanie `TrackInflight`/`ReleaseInflight` per message, health alert at 90%. Testy: credit exhaustion blokuje consume, release przywraca flow, health status degradation.
  -> [internal-components.md](../architecture/architecture/internal-components.md), [ADR-004](../architecture/decisions/ADR-004-credit-based-flow-control.md)

- [ ] **1.22. Fix: Enkapsulacja PooledBuffer w InboundMessage — usunięcie z publicznego API** `[unit]`
  Diagnosis: `PooledBuffer` (byte[]?) jest publiczną właściwością `InboundMessage` w BareWire.Abstractions, co wycieka szczegół implementacyjny (ArrayPool) i umożliwia use-after-free po settlement. Wymaga: (1) przeniesienie zarządzania lifetime bufora do samego `InboundMessage` (np. `IDisposable` + wewnętrzne `ReturnBuffer()`), (2) zmiana `PooledBuffer` na `internal` lub usunięcie z publicznego API, (3) aktualizacja `ReceiveEndpointRunner.cs` (finally block), `RabbitMqConsumer.cs` i wszystkich testów tworzących `InboundMessage` z `pooledBuffer`, (4) aktualizacja contract test baseline (`BareWire.Abstractions.approved.txt`).
  Regression tests required: reproduce use-after-free scenario + verify buffer is returned on Dispose.
  Requires: update contracts/public-api.md — PooledBuffer removal from public surface.
  -> [public-api.md](../architecture/api/public-api.md), [ADR-003](../architecture/decisions/ADR-003-zero-copy-pipeline.md)

---

## Funkcjonalność 2: Serializacja

> Faza 2 z planu implementacji. Zero-copy serializacja JSON z System.Text.Json.
> Kryterium zakończenia: serializacja < 128 B/msg; deserializacja < 256 B/msg.

- [x] **2.1. Zaimplementuj SystemTextJsonSerializer (zero-copy publish path)** `[unit]`
  `IMessageSerializer` z `Utf8JsonWriter` → `IBufferWriter<byte>` (pooled). Serializacja typowanego obiektu C# do raw JSON. Zero-alloc w steady-state. Content-Type: `application/json`. Testy: roundtrip proste/zagnieżdżone obiekty, record types.
  -> [internal-components.md](../architecture/architecture/internal-components.md), [TDD.md sekcja 15](../TDD.md)

- [x] **2.2. Zaimplementuj SystemTextJsonRawDeserializer (zero-copy consume path)** `[unit]`
  `IMessageDeserializer` z `Utf8JsonReader` ← `ReadOnlySequence<byte>`. Deserializacja raw JSON bez koperty. Zero-copy: brak kopiowania bufora. Testy: deserializacja z single/multi-segment sequences, edge cases (null, empty payload).
  -> [internal-components.md](../architecture/architecture/internal-components.md), [TDD.md sekcja 15](../TDD.md)

- [x] **2.3. Zaimplementuj BareWireEnvelopeSerializer** `[unit]`
  Serializacja/deserializacja pełnej koperty BareWire (`application/vnd.barewire+json`): MessageId, CorrelationId, ConversationId, MessageType URN, SentTime, Headers, Body. Opt-in przez konfigurację. Testy: roundtrip envelope, metadata preservation.
  -> [TDD.md sekcja 6](../TDD.md)

- [x] **2.4. Zaimplementuj Content-Type routing i rejestr deserializerów** `[unit]`
  Router: `application/json` → `SystemTextJsonRawDeserializer`, `application/vnd.barewire+json` → `BareWireEnvelopeDeserializer`, custom → rejestrowany per endpoint. Kaskada routingu typu: header `BW-MessageType` → routing key → single consumer → `IRawConsumer` fallback → reject/DLQ. Testy: routing po Content-Type, fallback scenarios.
  -> [TDD.md sekcja 6.2](../TDD.md)

- [x] **2.5. Dodaj testy jednostkowe serializacji** `[unit]`
  Pokrycie: roundtrip typowany/raw, null properties, empty body, large payload (> 64 KB), zagnieżdżone obiekty, kolekcje, record types, polimorfizm. Edge cases: invalid JSON → `BareWireSerializationException`.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **2.6. Dodaj benchmark serializacji** `[no-test]`
  `SerializationBenchmarks`: serialize/deserialize dla payloadów 100 B, 1 KB, 10 KB, 100 KB. Target: serialize < 128 B/msg alloc, deserialize < 256 B/msg alloc, < 1 μs per 1 KB.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

---

## Funkcjonalność 3: Transport RabbitMQ

> Faza 3 z planu implementacji. Działający adapter RabbitMQ z manualną topologią.
> Kryterium zakończenia: testy integracyjne z prawdziwym RabbitMQ (Aspire) przechodzą.

- [x] **3.1. Zaimplementuj RabbitMqTransportAdapter (połączenie i lifecycle)** `[integration]`
  `ITransportAdapter` z `RabbitMQ.Client` 7.x (async-native). `ConnectAsync` z connection string. `DisconnectAsync` z graceful drain. Automatic reconnect z exponential backoff. `TransportCapabilities`: PublisherConfirms, DlqNative, FlowControl. Test: connect/disconnect/reconnect.
  -> [TDD.md sekcja 8.2.1](../TDD.md), [internal-components.md](../architecture/architecture/internal-components.md)

- [x] **3.2. Zaimplementuj RabbitMqTopologyConfigurator i DeployTopologyAsync** `[integration]`
  `DeclareExchange` (name, type, durable, autoDelete), `DeclareQueue` (name, durable, DLX, TTL), `BindExchangeToQueue` (exchange, queue, routing key, arguments), `BindExchangeToExchange`. `DeployTopologyAsync` — topology-only deploy (bez uruchamiania konsumentów). Test: deploy exchange + queue + binding, idempotentność.
  -> [TDD.md sekcja 7](../TDD.md)

- [x] **3.3. Zaimplementuj send batch z publisher confirms** `[integration]`
  `SendBatchAsync(ReadOnlyMemory<OutboundMessage>)`: buforowanie + `basic.publish` z publisher confirms. Konfigurowalny linger time. `SendResult` z potwierdzeniem. Test: batch publish, confirm timeout, reconnect during publish.
  -> [TDD.md sekcja 8.2.1](../TDD.md)

- [x] **3.4. Zaimplementuj consume (IAsyncEnumerable z credit-based prefetch)** `[integration]`
  `ConsumeAsync(endpoint, flowControl)` → `IAsyncEnumerable<InboundMessage>`. `basic.qos(prefetchCount)` mapowany na `FlowControlOptions.MaxInFlightMessages`. Asynchroniczny consumer z `IAsyncBasicConsumer`. Test: consume z prefetch, flow control activation.
  -> [TDD.md sekcja 8.2.1](../TDD.md), [ADR-004](../architecture/decisions/ADR-004-credit-based-flow-control.md)

- [x] **3.5. Zaimplementuj settlement (ack, nack, reject, requeue)** `[integration]`
  `SettleAsync(SettlementAction, InboundMessage)`: Ack → `basic.ack`, Nack → `basic.nack`, Reject → `basic.reject(requeue=false)`, Requeue → `basic.nack(requeue=true)`. Atomic inflight release po settlement. Test: each settlement action, batch ack.
  -> [TDD.md sekcja 8.2.1](../TDD.md)

- [x] **3.6. Zaimplementuj RabbitMqHeaderMapper** `[unit]`
  Domyślne mapowania: `message-id` → MessageId, `correlation-id` → CorrelationId, header `BW-MessageType` → MessageType, `content-type` → ContentType, `traceparent` → TraceContext. Custom mappings przez `IHeaderMappingConfigurator`: `MapCorrelationId`, `MapMessageType`, `MapHeader` z transformacją. `IgnoreUnmappedHeaders` (whitelist). Testy: domyślne mapowania, custom mappings, unmapped headers.
  -> [TDD.md sekcja 6.4](../TDD.md), [security-architecture.md](../architecture/security/security-architecture.md)

- [x] **3.7. Zaimplementuj konfigurację TLS / mTLS** `[integration]`
  `UseTls(Action<ITlsConfigurator>)`: `CertificatePath`, `CertificatePassword`, `UseMutualAuthentication()`, `ServerCertificateValidation`. Konfiguracja `amqps://` URI. Test: połączenie z TLS (self-signed cert w kontenerze testowym).
  -> [security-architecture.md](../architecture/security/security-architecture.md)

- [x] **3.8. Zaimplementuj RabbitMqHostConfigurator (fluent API)** `[unit]`
  `UseRabbitMQ(Action<IRabbitMqConfigurator>)`: `Host(uri, configure)`, `ConfigureTopology(configure)`, `ReceiveEndpoint(queue, configure)`. `IHostConfigurator`: `Username`, `Password`, `UseTls`. Walidacja: URI format, wymagany host. Testy: konfiguracja poprawna, walidacja błędnych wartości.
  -> [configuration.md](../architecture/api/configuration.md)

- [x] **3.9. Zaimplementuj IRequestClient z temporary response queue** `[integration]`
  `CreateRequestClient<TRequest>()` → tworzy exclusive auto-delete queue dla odpowiedzi. `GetResponseAsync<TResponse>()` z timeout (`TaskCompletionSource` + `CancellationToken`). ReplyTo header z adresem temp queue. `RequestTimeoutException` po upływie timeout. Test: request-response E2E, timeout scenario.
  -> [public-api.md](../architecture/api/public-api.md), [TDD.md sekcja 5.5](../TDD.md)

- [x] **3.10. Dodaj testy integracyjne E2E z RabbitMQ** `[e2e]`
  Scenariusze: (1) Typowany publish/consume E2E, (2) Raw publish/consume z custom headers, (3) Topology deploy i ponowne deploy (idempotentność), (4) Retry + DLQ po wyczerpaniu prób, (5) Request-response z timeout, (6) Multiple consumers na jednym endpoint. Wszystkie z prawdziwym RabbitMQ przez Aspire.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **3.11. Fix: RabbitMqRequestClientFactory blokuje synchronicznie — ryzyko deadlocka** `[unit]`
  Diagnoza: `CreateRequestClient<T>()` używa `.GetAwaiter().GetResult()` i `_connectionLock.Wait()` z `CancellationToken.None`. W ASP.NET Core z sync context powoduje deadlock. Pod niedostępnością brokera blokuje wątki thread pool bez timeout.
  Wymaga: zmiana interfejsu `IRequestClientFactory.CreateRequestClient<T>` na `ValueTask<IRequestClient<T>> CreateRequestClientAsync<T>()`, lub krótkoterminowo opakowanie w `Task.Run` z timeout.
  Wymaga: aktualizacja `public-api.md` i `BareWire.Abstractions.approved.txt`.
  Regression tests required: reproduce deadlock scenario + timeout behavior.
  -> [public-api.md](../architecture/api/public-api.md)

- [x] **3.12. Fix: _disposed i _connection w RabbitMqRequestClientFactory nie są thread-safe** `[unit]`
  Diagnoza: `_disposed` to zwykły `bool` bez `volatile`/`Interlocked`. Double-checked locking na `_connection` czyta go poza lockiem bez `volatile`. Równoległe `CreateRequestClient` + `DisposeAsync` to data race.
  Wymaga: redesign synchronizacji — `volatile bool _disposed`, `volatile IConnection? _connection`, lub pełna ochrona odczytów wewnątrz locka.
  Regression tests required: concurrent CreateRequestClient + DisposeAsync race detection.
  -> [public-api.md](../architecture/api/public-api.md)

---

## Funkcjonalność 4: SAGA Engine

> Faza 4 z planu implementacji. Wbudowany SAGA state machine z persystencją.
> Kryterium zakończenia: OrderSagaStateMachine działa end-to-end z persystencją EF Core.

### BareWire.Saga

- [x] **4.1. Zaimplementuj BareWireStateMachine<T> z fluent API** `[unit]`
  Klasa bazowa: `Event<T>()`, `State()`, `Schedule<T>()`. Definicja przejść: `During(state, activities)`, `Initially(activities)`, `DuringAny(activities)`, `Finally(finalizer)`. Korelacja: `CorrelateBy<T>(event, selector)`, `CorrelateBy<T>(event, expression)`. Testy: definicja state machine, przejścia stanów, korelacja.
  -> [TDD.md sekcja 11.2](../TDD.md)

- [x] **4.2. Zaimplementuj IEventActivityBuilder z akcjami SAGA** `[unit]`
  `TransitionTo(state)`, `Then(action)`, `Publish<T>(factory)`, `Send<T>(destination, factory)`, `ScheduleTimeout(schedule, delay)`, `CancelTimeout(schedule)`, `Finalize()`. Fluent chain. Integracja z outbox (publish/send buforowane). Testy: chain building, each activity type.
  -> [TDD.md sekcja 11.3](../TDD.md)

- [x] **4.3. Zaimplementuj StateMachineExecutor<T>** `[unit]`
  Execution engine: load state → match event → execute activities → persist state. Optimistic concurrency: retry na `ConcurrencyException`. Testy: state transitions, event mismatch (ignore), concurrent modification.
  -> [TDD.md sekcja 11.1](../TDD.md)

- [x] **4.4. Zaimplementuj SagaEventRouter i CorrelationProvider** `[unit]`
  `SagaEventRouter`: routing eventów do instancji SAGA po korelacji. `CorrelationProvider`: `CorrelateById` (Guid z payloadu), `CorrelateByExpression` (dla queryable repo). SAGA z raw events: korelacja po polu z JSON payloadu. Testy: routing po CorrelationId, expression correlation, missing correlation → error.
  -> [TDD.md sekcja 11.2, 11.7](../TDD.md)

- [x] **4.5. Zaimplementuj strategie schedulingu timeoutów** `[unit]`
  `IScheduleConfigurator` z `Delay` i `Strategy`. Strategie: `Auto` (wybiera najlepszą), `DelayRequeue` (TTL + DLX na dedykowaną delay queue — bez pluginu), `TransportNative` (ASB), `ExternalScheduler` (Quartz/Hangfire), `DelayTopic` (Kafka). Dla MVP: implementacja `DelayRequeue` dla RabbitMQ. Testy: Auto selection, delay requeue flow.
  -> [TDD.md sekcja 11.4](../TDD.md)

- [x] **4.6. Zaimplementuj RoutingSlipExecutor i ICompensableActivity** `[unit]`
  `ICompensableActivity<TArguments, TLog>`: `ExecuteAsync` (forward), `CompensateAsync` (rollback). `RoutingSlipExecutor`: execute chain of activities; on failure → compensate in reverse. `CompensationLog` persistence. Testy: happy path, failure + compensation, partial failure.
  -> [TDD.md sekcja 11.8](../TDD.md)

- [x] **4.7. Zaimplementuj InMemorySagaRepository<T>** `[unit]`
  `ISagaRepository<T>` z `ConcurrentDictionary<Guid, TSaga>`. Optimistic concurrency per key (Version check). Do testów/dev (volatile). Testy: CRUD, concurrency conflict, delete.
  -> [TDD.md sekcja 11.5](../TDD.md)

### BareWire.Saga.EntityFramework

- [x] **4.8. Zaimplementuj EfCoreSagaRepository<T> z optimistic concurrency** `[integration]`
  `ISagaRepository<T>` i `IQueryableSagaRepository<T>` z EF Core 10. Optimistic concurrency: `Version` property mapowany na `RowVersion` (SQL Server) lub `xmin` (PostgreSQL). `FindAsync`, `SaveAsync`, `UpdateAsync` z concurrency check. `FindSingleAsync` z expression predicate. Test: CRUD, concurrency conflict → `ConcurrencyException`.
  -> [TDD.md sekcja 11.5](../TDD.md)

- [x] **4.9. Utwórz SagaDbContext z migracjami EF Core** `[no-test]`
  `SagaDbContext` z konfigurowanym mapowaniem TSaga. Indeks na `CorrelationId`. Concurrency token na `Version`. Migracja initial create. Konfiguracja per-saga registration.
  -> [TDD.md sekcja 11.5](../TDD.md)

### Testy SAGA

- [x] **4.10. Dodaj testy integracyjne: SAGA E2E z RabbitMQ + EF Core** `[e2e]`
  Scenariusz OrderSaga: (1) OrderCreated → state: Processing, (2) PaymentReceived → state: Completed + publish OrderCompleted, (3) PaymentFailed → state: Compensating → Failed. Concurrency: 10 instancji, 100 eventów per instancja, < 5% conflicts. Timeout: Schedule → fire after delay. Kompensacja: RoutingSlip 3 activities → failure → compensate.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **4.11. Fix: Saga dispatchers resolved from a scope that's immediately disposed** `[unit]`
  Diagnoza: `BareWireBusControl.StartAsync` (linia 94-97) — `using IServiceScope dispatcherScope` rozwiązuje `ISagaMessageDispatcher` instances, a następnie scope jest disposed na końcu `StartAsync`. Dispatchery są przekazywane do `ReceiveEndpointRunner` i używane przez cały czas życia busa. Jeśli zarejestrowane jako Scoped, rzucą `ObjectDisposedException` przy pierwszym użyciu.
  Wymaga: redesign DI lifetime — zarejestrować saga dispatchers jako Singleton (trzymają tylko singleton-safe deps: `IServiceScopeFactory`, `ILoggerFactory`), lub rozwiązywać per-message.
  Regression tests required: verify dispatchers survive beyond StartAsync scope, verify per-message scoping if chosen.
  -> [internal-components.md](../architecture/architecture/internal-components.md)

- [x] **4.12. Dodaj AutoCreateSchema do Saga (IHostedService + parametr w rejestracji)** `[integration]`
  Nowy `SagaSchemaInitializer : IHostedService` — przy starcie woła `IRelationalDatabaseCreator.CreateTablesAsync()` na `SagaDbContext` (idempotentnie, catch na "table already exists"). Dodanie opcjonalnego parametru `bool autoCreateSchema = false` do `AddBareWireSaga<TSaga>()`. Rejestrowany warunkowo gdy `autoCreateSchema == true`. Testy: initializer rejestrowany tylko gdy parametr = true, tabele tworzone automatycznie przy starcie hosta.
  -> [TDD.md sekcja 11.5](../TDD.md)

---

## Funkcjonalność 5: Outbox / Inbox

> Faza 5 z planu implementacji. Gwarancje exactly-once w pipeline.
> Kryterium zakończenia: transactional outbox — business state + outbox w jednej transakcji DB.

### BareWire.Outbox

- [x] **5.1. Zaimplementuj InMemoryOutboxMiddleware** `[unit]`
  `IMessageMiddleware`: buforowanie `PublishAsync`/`SendAsync` w pamięci. Po sukcesie handlera: `FlushAsync()` → `SendBatch` do adaptera → ack original. Przy wyjątku: `Discard()` bufor → nack/abandon original. Testy: buffer + flush, discard on error, nested publish.
  -> [TDD.md sekcja 12.1](../TDD.md)

- [x] **5.2. Zaimplementuj OutboxDispatcher (background polling service)** `[unit]`
  Background `IHostedService`: polling loop (konfigurowalny interval, domyślnie 1s). `SELECT` pending outbox messages (batch size konfigurowalny, domyślnie 100). Publish → update status = delivered. Idempotent: skip already delivered. Testy: polling, batch dispatch, idempotency.
  -> [TDD.md sekcja 12.2](../TDD.md)

- [x] **5.3. Zaimplementuj InboxFilter (deduplication)** `[unit]`
  `TryLock(MessageId)`: sprawdzenie czy wiadomość była już przetworzona. Already-processed → ack (idempotent skip). Lock timeout (konfigurowalny, domyślnie 30s). Testy: first process → lock, duplicate → skip, expired lock.
  -> [TDD.md sekcja 12.2](../TDD.md)

- [x] **5.4. Zaimplementuj konfigurację retention i cleanup** `[unit]`
  `IOutboxConfigurator`: `InboxRetention` (domyślnie 7 dni), `OutboxRetention` (domyślnie 7 dni), `PollingInterval`, `DispatchBatchSize`, `InboxLockTimeout`. Background cleanup job: usuwanie rekordów starszych niż retention. Testy: retention calculation, cleanup logic.
  -> [TDD.md sekcja 12.3](../TDD.md)

### BareWire.Outbox.EntityFramework

- [x] **5.5. Utwórz entity OutboxMessage i InboxMessage z migracjami EF Core** `[no-test]`
  `OutboxMessage`: Id (PK auto), MessageId (Guid, indexed), Body (byte[]), ContentType, DestinationAddress, Headers (JSON), Status (Pending/Delivered), CreatedAt, DeliveredAt. `InboxMessage`: Id (PK auto), MessageId (Guid, unique indexed), ProcessedAt, ExpiresAt. Migracja initial create z indeksami.
  -> [TDD.md sekcja 12.4](../TDD.md)

- [x] **5.6. Zaimplementuj TransactionalOutboxMiddleware** `[integration]`
  `IMessageMiddleware`: inbox check (TryLock MessageId) → handler execution → business state + outbox messages w jednej transakcji EF Core `SaveChangesAsync()` → ack original. Rollback on error. Testy: exactly-once (business + outbox atomic), inbox dedup, rollback.
  -> [TDD.md sekcja 12.2](../TDD.md)

- [x] **5.7. Utwórz OutboxDbContext** `[no-test]`
  DbContext z DbSet<OutboxMessage>, DbSet<InboxMessage>. Konfiguracja mapowania, indeksów. Rejestracja w DI z konfiguracją connection string.
  -> [TDD.md sekcja 12.4](../TDD.md)

### Testy Outbox

- [x] **5.8. Dodaj testy integracyjne: transactional outbox z EF Core + RabbitMQ** `[e2e]`
  Scenariusze: (1) Happy path: consume → business save + outbox → dispatcher delivers, (2) Inbox dedup: ten sam MessageId dwa razy → przetworzenie 1 raz, (3) Handler failure → rollback (brak outbox records), (4) Dispatcher retry: broker down → pending → broker up → delivered. (5) Retention cleanup: stare records usunięte.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **5.9. Dodaj AutoCreateSchema do Outbox (IHostedService + flaga w konfiguratorze)** `[integration]`
  Dodanie `AutoCreateSchema` (bool, default: false) do `IOutboxConfigurator` i `OutboxOptions`. Nowy `OutboxSchemaInitializer : IHostedService` — przy starcie woła `IRelationalDatabaseCreator.CreateTablesAsync()` (idempotentnie, catch na "table already exists"). Rejestrowany warunkowo w `ServiceCollectionExtensions.AddBareWireOutbox()` gdy `AutoCreateSchema == true`. Usunąć ręczne tworzenie tabel z `BareWire.Samples.TransactionalOutbox/Program.cs` i zastąpić `outbox.AutoCreateSchema = true`. Testy: initializer rejestrowany tylko gdy flaga = true, tabele tworzone automatycznie przy starcie hosta.
  -> [TDD.md sekcja 12](../TDD.md)

---

## Funkcjonalność 6: Observability

> Faza 6 z planu implementacji. OpenTelemetry-first tracing + metrics + health checks.
> Kryterium zakończenia: spany widoczne w Aspire Dashboard; metryki w OTel.

- [x] **6.1. Zaimplementuj BareWireActivitySource (OTel tracing)** `[unit]`
  `ActivitySource("BareWire")`: spany publish (producer), consume (consumer), saga.transition, outbox.dispatch. Tags: `messaging.system`, `messaging.destination`, `messaging.message_id`, `messaging.correlation_id`. Kind: Producer/Consumer. Testy: span creation, tag values, parent-child relationship.
  -> [TDD.md sekcja 13](../TDD.md)

- [x] **6.2. Zaimplementuj BareWireMetrics (OTel counters i histogramy)** `[unit]`
  `Meter("BareWire")`: Counters: `barewire.messages.published`, `barewire.messages.consumed`, `barewire.messages.failed`, `barewire.messages.dead_lettered`. Histogramy: `barewire.message.duration` (processing time), `barewire.message.size` (payload bytes). Tags: endpoint, message_type. Testy: counter increment, histogram record.
  -> [TDD.md sekcja 13](../TDD.md)

- [x] **6.3. Zaimplementuj propagację trace context (traceparent header)** `[integration]`
  Publish: inject `Activity.Current` → header `traceparent` w transport headers. Consume: extract `traceparent` z headers → create child span z propagowanym trace context. W3C TraceContext format. Test: publish → consume → verify trace_id propagation.
  -> [TDD.md sekcja 13](../TDD.md), [security-architecture.md](../architecture/security/security-architecture.md)

- [x] **6.4. Zaimplementuj BareWireEventCounterSource** `[unit]`
  `EventSource("BareWire")` z `EventCounters`: inflight-messages, channel-utilization, publish-rate, consume-rate, alloc-rate. Konfigurowalny interval (domyślnie 5s). Low-overhead mode (< 1% narzutu). Testy: counter publishing, interval.
  -> [TDD.md sekcja 13](../TDD.md)

- [x] **6.5. Zaimplementuj BareWireHealthCheck** `[integration]`
  `IHealthCheck`: broker connection status, inflight load (> 90% → Degraded), outbox pending count (> threshold → Degraded), saga stuck detection (state unchanged > timeout → Degraded). Redacted connection strings w output (SEC-06). Test: healthy, degraded (high inflight), unhealthy (broker down).
  -> [TDD.md sekcja 13](../TDD.md), [security-architecture.md](../architecture/security/security-architecture.md)

- [x] **6.6. Dodaj integrację z Aspire Dashboard** `[integration]`
  `ConfigureObservability(Action<IObservabilityConfigurator>)`: `EnableOpenTelemetry` (default: true), `EnableEventCounters` (default: true). Konfiguracja OTel exporter (OTLP). Weryfikacja spanów i metryk w Aspire Dashboard. Test: spany publish/consume widoczne w dashboardzie.
  -> [TDD.md sekcja 13](../TDD.md)

- [x] **6.7. Dodaj testy integracyjne observability** `[integration]`
  Pokrycie: (1) spany publish → consume z poprawnym trace_id, (2) metryki: counter increment po publish/consume, (3) health check: healthy/degraded/unhealthy, (4) EventCounters: inflight tracking. In-process OTel collector w testach.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

---

## Funkcjonalność 7: Kontrakty + Benchmarki + Samples

> Faza 7 z planu implementacji. Stabilność API, performance validation, dokumentacja samples.
> Kryterium zakończenia: wszystkie testy przechodzą; benchmarki spełniają target; sample działa.

### Contract Tests

- [x] **7.1. Zaimplementuj PublicApiTests z PublicApiGenerator** `[unit]`
  Snapshot publicznego API: `BareWire.Abstractions`, `BareWire` (publiczne extension methods). Porównanie z `.approved.txt`. Fail na breaking change (usunięcie/zmiana sygnatury publicznej metody). Test: generate → compare → fail on diff.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **7.2. Zaimplementuj ArchitectureRuleTests z NetArchTest** `[unit]`
  Reguły: Transport !→ BareWire, Observability !→ BareWire, Abstractions !→ {BareWire, Transport, Observability, Saga, Outbox}, Serialization !→ {BareWire, Transport}, Saga → {Abstractions, BareWire} only. Test per reguła z `Types.InAssembly().ShouldNot().HaveDependencyOn()`.
  -> [CONSTITUTION.md](../architecture/CONSTITUTION.md), [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **7.3. Wygeneruj .approved.txt dla wszystkich publicznych pakietów** `[no-test]`
  `BareWire.Abstractions.approved.txt`, `BareWire.approved.txt` (extension methods). Baseline dla przyszłych breaking change checks. Commit do repo.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

### Benchmarki

- [x] **7.4. Dodaj pełny benchmark suite** `[no-test]`
  `PublishBenchmarks`: typowany in-memory, raw in-memory, typowany RabbitMQ. `ConsumeBenchmarks`: consume + ack in-memory, consume + ack RabbitMQ. `SerializationBenchmarks`: serialize/deserialize 100 B — 100 KB. `SagaBenchmarks`: state transition in-memory. Wszystkie z `[MemoryDiagnoser]` i `[EventPipeProfiler]`.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **7.5. Zwaliduj performance targets vs baseline** `[no-test]`
  Publish typowany: > 500K msgs/s, < 128 B/msg. Publish raw: > 1M msgs/s, 0 B/msg. Consume + ack: > 300K msgs/s, < 256 B/msg. SAGA transition: > 100K msgs/s, < 512 B/msg. JSON serialize 1 KB: < 1 μs, < 128 B. JSON deserialize 1 KB: < 1 μs, < 256 B.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

### Samples i CI

- [x] **7.6. Utwórz BareWire.Samples.RabbitMQ** `[no-test]`
  Przykładowa aplikacja ASP.NET Core: POST /orders → publish OrderCreated. Consumer → process → publish OrderProcessed. SAGA: OrderSaga z tranzycjami. Outbox: transactional outbox z EF Core. Config: RabbitMQ + Aspire + health checks + OTel.
  -> [usage/getting-started.md](../architecture/usage/getting-started.md)

- [x] **7.7. Rozszerz CI pipeline o contract check i benchmark gate** `[no-test]`
  GitHub Actions: contract test failure → block merge. Benchmark regresja > 10% alokacji → warning (nie block w MVP). Artifact: benchmark results jako CI artifact.
  -> [implementation-plan.md](../architecture/implementation-plan.md)

### Scenariusze E2E

- [x] **7.8. Dodaj obowiązkowe scenariusze E2E z testing-spec.md** `[e2e]`
  E2E-1: steady-state throughput 10 min (< 256 B/msg, 0 GC Gen2). E2E-2: spike 10x → backpressure → recovery < 30s. E2E-3: retry storm + outbox/inbox (50% failures) → exactly-once. E2E-4: SAGA concurrency (1000 events → 10 instancji, < 5% conflicts). E2E-5: large payloads > 256 KB.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

---

## Funkcjonalność 8: Samples — scenariusze użycia

> Kompletne przykłady użycia BareWire w rzeczywistych scenariuszach.
> Każdy sample to osobny projekt ASP.NET Core z Aspire AppHost, prawdziwym RabbitMQ i PostgreSQL.
> Kryterium zakończenia: każdy sample kompiluje się, uruchamia przez Aspire i działa end-to-end.

### Infrastruktura Aspire

- [x] **8.1. Utwórz BareWire.Samples.AppHost (Aspire orchestrator)** `[no-test]`
  Aspire AppHost orkiestrujący wszystkie sample'e. Resources: RabbitMQ (kontener Docker), PostgreSQL (kontener Docker). Referencje do każdego sample'a z automatycznym discovery connection stringów (`RabbitMQ` i `PostgreSQL`). Dashboard Aspire z widocznością traces/metrics/logs. Wspólny `docker-compose.yml` jako fallback bez Aspire.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **8.2. Utwórz BareWire.Samples.ServiceDefaults (wspólna konfiguracja)** `[no-test]`
  Shared project z domyślną konfiguracją dla wszystkich sample'ów: OpenTelemetry (traces + metrics + OTLP exporter do Aspire Dashboard), health checks (`/health`, `/health/live`, `/health/ready`), `AddServiceDefaults()` extension method. Referencje do `BareWire.Observability`.
  -> [configuration.md](../architecture/api/configuration.md)

### Scenariusz: Podstawowy Publish / Consume

- [x] **8.3. Utwórz BareWire.Samples.BasicPublishConsume** `[no-test]`
  Najprostszy możliwy scenariusz. API: `POST /messages` → publish `MessageSent` do RabbitMQ. Consumer: `MessageConsumer` odbiera `MessageSent`, loguje i zapisuje do tabeli `ReceivedMessages` w PostgreSQL. API: `GET /messages` → lista odebranych wiadomości z bazy. Ręczna topologia (exchange fanout + queue). Konfiguracja: Aspire discovery dla RabbitMQ i PostgreSQL. EF Core + Npgsql. README z instrukcją uruchomienia.
  -> [usage/getting-started.md](../architecture/usage/getting-started.md)

### Scenariusz: Request-Response

- [x] **8.4. Utwórz BareWire.Samples.RequestResponse** `[no-test]`
  Wzorzec request-response z `IRequestClient<T>`. API: `POST /validate-order` → wysyła `ValidateOrder` request przez `IRequestClient<ValidateOrder>` → czeka na `OrderValidationResult` response (timeout 10s). Consumer: `OrderValidationConsumer` na osobnej kolejce — waliduje zamówienie, odpowiada `RespondAsync<OrderValidationResult>`. PostgreSQL: zapis historii walidacji. Demonstracja: `RequestTimeoutException` gdy consumer jest wyłączony. README z diagramem sekwencji.
  -> [public-api.md](../architecture/api/public-api.md)

### Scenariusz: Raw Message Interop

- [x] **8.5. Utwórz BareWire.Samples.RawMessageInterop** `[no-test]`
  Konsumpcja wiadomości z zewnętrznego systemu (raw JSON, bez koperty BareWire). Symulacja: osobny `BackgroundService` publikuje raw JSON do RabbitMQ (bezpośrednio `RabbitMQ.Client`, bez BareWire). Consumer: `IRawConsumer` odbiera surowy payload, deserializuje przez `TryDeserialize<T>()`, mapuje na domenowy model. Drugi consumer: typowany `IConsumer<T>` z automatycznym Content-Type routing. Custom `IHeaderMappingConfigurator`: mapowanie nagłówków zewnętrznego systemu (`X-Correlation-Id` → `CorrelationId`, `X-Message-Type` → `MessageType`). PostgreSQL: zapis przetworzonych wiadomości. README opisujący scenariusz migracji z legacy systemu.
  -> [TDD.md sekcja 6](../TDD.md), [ADR-001](../architecture/decisions/ADR-001-raw-first-serialization.md)

### Scenariusz: SAGA State Machine

- [x] **8.6. Utwórz BareWire.Samples.SagaOrderFlow** `[no-test]`
  Kompletny przepływ zamówienia z SAGA. `OrderSagaStateMachine`: Initial → `OrderCreated` → Processing → `PaymentReceived` → Shipping → `ShipmentDispatched` → Completed. Alternatywna ścieżka: Processing → `PaymentFailed` → Compensating → `CompensationCompleted` → Failed. Timeout: `Schedule<PaymentTimeout>` po 30s → automatyczne anulowanie. `RoutingSlip`: 3 aktywności kompensowalne (ReserveStock → ChargePayment → CreateShipment) z rollback. API: `POST /orders`, `GET /orders/{id}/status` (query saga state z PostgreSQL). PostgreSQL: `EfCoreSagaRepository<OrderSagaState>` z optimistic concurrency (`xmin`). Aspire Dashboard: widoczne trace'y przejść stanów. README z diagramem stanów.
  -> [TDD.md sekcja 11](../TDD.md)

### Scenariusz: Transactional Outbox / Inbox

- [x] **8.7. Utwórz BareWire.Samples.TransactionalOutbox** `[no-test]`
  Exactly-once delivery z transactional outbox. API: `POST /transfers` → tworzy `Transfer` w PostgreSQL + `TransferInitiated` w outbox — jedna transakcja EF Core `SaveChangesAsync()`. `OutboxDispatcher`: polling co 1s, batch dispatch do RabbitMQ. Consumer: `TransferConsumer` z `InboxFilter` (deduplication po `MessageId`). Demonstracja: (1) Zabicie RabbitMQ w trakcie → wiadomości czekają w outbox → po restarcie dostarczane. (2) Duplikat `MessageId` → inbox odrzuca. API: `GET /transfers` — lista transferów z bazy, `GET /outbox/pending` — podgląd pending messages. PostgreSQL: `OutboxDbContext` + biznesowy `TransferDbContext` (shared connection). Retention cleanup co 60s. README z diagramem exactly-once flow.
  -> [TDD.md sekcja 12](../TDD.md)

### Scenariusz: Retry + Dead Letter Queue

- [x] **8.8. Utwórz BareWire.Samples.RetryAndDlq** `[no-test]`
  Obsługa błędów z retry i DLQ. Consumer: `PaymentProcessor` rzuca `PaymentDeclinedException` w 70% przypadków (symulacja). `RetryMiddleware`: 3 próby, exponential backoff (1s, 2s, 4s). `DeadLetterMiddleware`: po wyczerpaniu retry → wiadomość trafia na `payments-dlq`. Drugi consumer: `DlqConsumer` na kolejce `payments-dlq` — loguje i zapisuje do tabeli `FailedPayments` w PostgreSQL. API: `POST /payments` → publish `ProcessPayment`, `GET /payments/failed` → lista z DLQ. Topology: exchange `payments` → queue `payments` z DLX do `payments-dlq`. README z opisem strategii retry.
  -> [error-handling.md](../architecture/api/error-handling.md)

- [x] **8.12. Fix: Wire retry and DLQ middleware into ReceiveEndpointRunner** `[integration]`
  Diagnosis: `EndpointBinding` nie zawiera pól `RetryCount`/`RetryInterval` — ustawienia z `RabbitMqEndpointConfiguration` są tracone przy konwersji (`RabbitMQ/ServiceCollectionExtensions.cs:64-73`). `ReceiveEndpointRunner` dispatchuje bezpośrednio przez `ConsumerInvokerFactory`, pomijając `MessagePipeline` — `RetryMiddleware` i `DeadLetterMiddleware` istnieją w Core ale nigdy nie są wywołane w ścieżce consume. Konsekwencja: consumer exception → natychmiastowy NACK, zero retry, zero DLQ routing na poziomie aplikacji.
  Fix wymaga: (1) dodanie `RetryCount`, `RetryInterval` do `EndpointBinding`, (2) przepływ retry settings z `RabbitMqEndpointConfiguration` do `EndpointBinding`, (3) integracja `MessagePipeline` (z `RetryMiddleware` + `DeadLetterMiddleware`) w `ReceiveEndpointRunner` consume loop.
  Regression tests required: reproduce the bug scenario (consumer throws → verify retry happens 3x → then DLQ routing) + edge cases (cancellation during retry, all attempts succeed on retry).
  -> [error-handling.md](../architecture/api/error-handling.md)

- [x] **8.13. Fix: Support queue arguments in ITopologyConfigurator.DeclareQueue** `[integration]`
  Diagnosis: `ITopologyConfigurator.DeclareQueue` nie wspiera queue arguments (np. `x-dead-letter-exchange`). Bez tego argumentu na kolejce `payments`, RabbitMQ nie routuje nackowanych wiadomości do `payments.dlx` — są kasowane. Ograniczenie udokumentowane w headerze sampla RetryAndDlq.
  Fix wymaga: (1) rozszerzenie `DeclareQueue` o opcjonalny `Dictionary<string, object>? arguments`, (2) propagacja argumentów do `TopologyDeclaration` i `RabbitMqTopologyDeployer`, (3) aktualizacja sampla RetryAndDlq aby deklarował queue z `x-dead-letter-exchange = "payments.dlx"`.
  Regression tests required: declare queue with arguments → verify arguments applied in topology deployment.
  -> [error-handling.md](../architecture/api/error-handling.md)

### Scenariusz: Backpressure i Flow Control

- [x] **8.9. Utwórz BareWire.Samples.BackpressureDemo** `[no-test]`
  Demonstracja mechanizmów flow control. API: `POST /load-test/start` → uruchamia `BackgroundService` publikujący 10K msg/s. Consumer: `SlowConsumer` z opóźnieniem 100ms per wiadomość (symulacja wolnego przetwarzania). `FlowControlOptions`: `MaxInFlightMessages = 50`, `MaxInFlightBytes = 1MB`. `PublishFlowControlOptions`: `MaxPendingPublishes = 500`. Health endpoint: `GET /health` → pokazuje `Degraded` przy > 90% capacity. API: `GET /metrics` → aktualne metryki (inflight count, publish queue depth, consumer lag). Demonstracja: consumer nie nadąża → backpressure → publisher zwalnia → system się stabilizuje. README z opisem credit-based flow control (ADR-004, ADR-006).
  -> [ADR-004](../architecture/decisions/ADR-004-credit-based-flow-control.md), [ADR-006](../architecture/decisions/ADR-006-publish-backpressure.md)

### Scenariusz: Observability Dashboard

- [x] **8.10. Utwórz BareWire.Samples.ObservabilityShowcase** `[no-test]`
  Showcase pełnego stosu observability. Aplikacja z wszystkimi komponentami: publish, consume, SAGA, outbox. OpenTelemetry: traces z propagacją `traceparent` (publish → consume → saga transition → outbox dispatch — pełny distributed trace). Metrics: `barewire.messages.published`, `barewire.messages.consumed`, `barewire.message.duration` histogramy. EventCounters: `inflight-messages`, `publish-rate`, `consume-rate`. Health checks: bus health + inflight load + outbox pending + saga stuck detection. API: `POST /demo/run` → uruchamia scenariusz generujący ruch na wszystkich komponentach. Aspire Dashboard: pełna widoczność traces, metrics, logs. PostgreSQL: SAGA state + outbox. README z instrukcją czytania traces w Aspire Dashboard.
  -> [TDD.md sekcja 13](../TDD.md)

### Scenariusz: Multi-Consumer i Partycjonowanie

- [x] **8.11. Utwórz BareWire.Samples.MultiConsumerPartitioning** `[no-test]`
  Wiele consumerów na jednym endpoint + partycjonowanie. Topology: exchange `events` (topic) → queue `event-processing` z 3 bindingami (routing keys: `order.*`, `payment.*`, `shipment.*`). 3 consumery: `OrderEventConsumer`, `PaymentEventConsumer`, `ShipmentEventConsumer` na jednej kolejce z Content-Type routing. `PartitionerMiddleware`: partycjonowanie po `CorrelationId` — wiadomości z tym samym `CorrelationId` przetwarzane sekwencyjnie. `ConcurrentMessageLimit = 16`, `partitionCount = 64`. API: `POST /events/generate` → generuje burst 1000 eventów z 10 unikalnymi `CorrelationId`. PostgreSQL: zapis kolejności przetwarzania (weryfikacja sekwencyjności per partition). `GET /events/processing-log` → log przetwarzania z timestampami. README z opisem partycjonowania i topic routing.
  -> [internal-components.md](../architecture/architecture/internal-components.md)

---

## Funkcjonalność 9: Refaktoryzacja

> Zadania porządkowe i refaktoryzacyjne wykryte w trakcie rozwoju projektu.

- [x] **9.1. Zmień nazwę projektu BareWire.Core na BareWire** `[no-test]`
  Refaktoryzacja nazwy: projekt `BareWire.Core` → `BareWire`, namespace `BareWire.Core` → `BareWire`. Obejmuje: rename katalogu `src/BareWire.Core/` → `src/BareWire/`, rename pliku `.csproj`, aktualizacja namespace we wszystkich plikach `.cs` w projekcie, aktualizacja referencji `<ProjectReference>` w zależnych projektach (`BareWire.Saga`, `BareWire.Outbox`, `BareWire.Testing`, projekty testowe), aktualizacja `BareWire.slnx`, aktualizacja `InternalsVisibleTo` w `BareWire.Abstractions.csproj`, aktualizacja `.approved.txt` w ContractTests, aktualizacja reguł `NetArchTest` w `ArchitectureRuleTests`, aktualizacja `Directory.Packages.props` jeśli wymaga, aktualizacja dokumentacji (`CLAUDE.md`, `CONSTITUTION.md`, `internal-components.md`). Weryfikacja: `dotnet build BareWire.slnx` + `dotnet test BareWire.slnx`.
  -> [CONSTITUTION.md](../architecture/CONSTITUTION.md)

---

## Funkcjonalność 10: Bugi wykryte w E2E testach samples

> Bugi wykryte podczas testów E2E wszystkich 11 samples uruchomionych razem via Aspire AppHost.
> Runda 1 (2026-03-26): bugi 10.1–10.3 (naprawione). Runda 2 (2026-03-27): bugi 10.4–10.6 (naprawione). Runda 3 (2026-03-27): bugi 10.7–10.9 (naprawione). Runda 4 (2026-03-27): bugi 10.10–10.12 (naprawione). Runda 5 (2026-03-27): bugi 10.13–10.16 (naprawione). Runda 6 (2026-03-27): bug 10.17. Runda 7 (2026-03-27): bugi 10.18–10.20. Runda 8 (2026-03-28): bug 10.21 (flaky test).

- [x] **10.1. OutboxDispatcher re-publishuje wiadomości w nieskończonej pętli** `[bug]` `[integration]`
  `OutboxDispatcher` (BareWire.Outbox.EntityFramework) po opublikowaniu wiadomości nie oznacza jej jako wysłanej w tabeli `OutboxMessages`. Przy każdym poll (co 1s) ponownie publikuje te same rekordy. Obserwowane: 86K+ publish na exchange `order.events` z rate ~1150/s po jednej wiadomości. Dotyczy `OutboxDispatcher` — po `PublishAsync` powinien ustawiać `DispatchedAt` lub usuwać rekord z outbox table.
  -> `src/BareWire.Outbox.EntityFramework/OutboxDispatcher.cs`
  -> [ADR-006](../architecture/decisions/ADR-006-publish-backpressure.md)

- [x] **10.2. Poison message jest permanentnie utracona gdy queue nie ma DLX** `[bug]` `[unit]`
  Gdy consumer rzuci exception (np. poison message z null content), `ReceiveEndpointRunner` wykonuje `BasicNackAsync(requeue: false)`. Jeśli queue nie ma skonfigurowanego `x-dead-letter-exchange`, wiadomość jest permanentnie utracona bez żadnego warning w logach. Framework powinien: (a) logować warning na poziomie `Warning` gdy Nack'd message nie ma DLX, (b) rozważyć domyślny retry (np. 1x) nawet bez explicit `RetryCount` — albo przynajmniej wyraźny log z informacją, że wiadomość zostanie utracona. Dotyczy `ReceiveEndpointRunner.DispatchMessageAsync` i `DeadLetterMiddleware`.
  -> `src/BareWire/Bus/ReceiveEndpointRunner.cs` (linia 221: `action = SettlementAction.Nack`)
  -> `src/BareWire/Pipeline/DeadLetterMiddleware.cs`

- [x] **10.3. Sample BasicPublishConsume nie ma retry ani DLQ — poison messages są tracone** `[bug]` `[no-test]`
  Sample `BareWire.Samples.BasicPublishConsume` konfiguruje endpoint bez `RetryCount`, bez `x-dead-letter-exchange` na queue. Gdy consumer dostanie niepoprawną wiadomość (np. `Content=null` → DB NOT NULL constraint), wiadomość jest cicho odrzucona. Sample powinien demonstrować best practice: `RetryCount=3`, `x-dead-letter-exchange` na queue `messages`, DLQ queue `messages-dlq`. Wzorzec: patrz `BareWire.Samples.RetryAndDlq`.
  -> `samples/BareWire.Samples.BasicPublishConsume/Program.cs`

- [x] **10.4. RabbitMQ Sample — nieskończona pętla publish (OrderConsumer → order.events → orders)** `[bug]` `[no-test]`
  `OrderConsumer` przetwarza `OrderCreated` i publikuje `OrderProcessed` via `context.PublishAsync()`. Ponieważ nie ma ustawionego headera `BW-Exchange`, wiadomość trafia na domyślny exchange `order.events` (fanout). Exchange `order.events` jest bindowany do queue `orders`, więc `OrderProcessed` wraca do `OrderConsumer`. Deserializacja raw JSON `OrderProcessed` jako `OrderCreated` udaje się (System.Text.Json ustawia brakujące pola na default: `Amount=0`, `Currency=null`), consumer przetwarza i publikuje następny `OrderProcessed` — nieskończona pętla z rate ~1300 msg/s. Obserwowane: 310K+ publish na exchange `order.events` w 90s. Efekt uboczny: queue `order-saga` (z sample SagaOrderFlow) też jest zalewany tymi wiadomościami. Fix: (a) OrderConsumer powinien publikować na osobny exchange (np. `order-processed.events`) lub (b) użyć topic exchange z routing key opartym na typie wiadomości zamiast fanout.
  -> `samples/BareWire.Samples.RabbitMQ/Consumers/OrderConsumer.cs` (linia 29: `context.PublishAsync`)
  -> `samples/BareWire.Samples.RabbitMQ/Program.cs` (linia 74: `rmq.DefaultExchange("order.events")`, linia 83: fanout exchange)

- [x] **10.5. RetryAndDlq — DlqConsumer traci wiadomości z DLQ przez null Currency (NOT NULL constraint)** `[bug]` `[no-test]`
  Endpoint `POST /payments` nie wymusza pola `Currency` w `PaymentRequest` — jest ono nullable w praktyce (JSON deserializacja ustawia `null`). Wiadomość `ProcessPayment` z `Currency=null` przechodzi retry, trafia do DLQ, i `DlqConsumer` próbuje zapisać `FailedPayment` z `Currency=null`. Entity `FailedPayment.Currency` jest `required string` (NOT NULL w PostgreSQL) → `DbUpdateException` → wiadomość Nackowana bez requeue. Queue `payments-dlq` nie ma swojego DLX → wiadomość permanentnie utracona. Obserwowane: 11 z 20 płatności trafiło do DLQ, 0 zapisanych w DB, 0 wiadomości w queue. Fix: (a) walidować Currency w HTTP endpoint (wymagać wartość lub default "USD"), (b) zmienić `FailedPayment.Currency` na nullable `string?`, (c) dodać DLX na queue `payments-dlq` jako zabezpieczenie.
  -> `samples/BareWire.Samples.RetryAndDlq/Consumers/DlqConsumer.cs` (linia 31: `Currency = payment.Currency`)
  -> `samples/BareWire.Samples.RetryAndDlq/Data/FailedPayment.cs` (linia 19: `required string Currency`)
  -> `samples/BareWire.Samples.RetryAndDlq/Program.cs` (linia 208: `PaymentRequest`)

- [x] **10.6. SagaOrderFlow — saga zwraca 404 po zakończeniu zamiast stanu „Finalized"** `[bug]` `[no-test]`
  Po pełnym cyklu SAGA (Processing → Shipping → Completed), `GET /orders/{id}/status` zwraca HTTP 404 „No saga found". Przyczyną jest `.Finalize()` w state machine, które usuwa sagę z repozytorium EF Core po przejściu do stanu końcowego. `.Finalize()` zostaje — to poprawne zachowanie produkcyjne. Fix: endpoint `GET /orders/{id}/status` zwraca HTTP 200 z `CurrentState = "Finalized"` zamiast 404 gdy saga została sfinalizowana i usunięta z bazy.
  -> `samples/BareWire.Samples.SagaOrderFlow/Program.cs` (endpoint GET /orders/{id}/status)

- [x] **10.7. ReceiveEndpointRunner nie wstrzykuje IMessageMiddleware z DI — TransactionalOutboxMiddleware martwy kod** `[bug]` `[integration]`
  `ReceiveEndpointRunner` buduje łańcuch middleware manualnie (linie 88–112): dodaje tylko `RetryMiddleware` i `DeadLetterMiddleware`. Nie resolve'uje `IMessageMiddleware` z kontenera DI. `TransactionalOutboxMiddleware` jest zarejestrowany w DI via `AddBareWireOutbox()` (jako `IMessageMiddleware` scoped), ale NIGDY nie wchodzi do pipeline przetwarzania wiadomości. Skutek: (a) inbox deduplication nie działa — tabela `InboxMessages` jest pusta, wiadomości przetwarzane wielokrotnie, (b) `TransactionScope` z middleware nigdy nie opakowuje consumer logic, (c) outbox buffer na consumer side nie działa. Fix: `ReceiveEndpointRunner` powinien resolve'ować `IEnumerable<IMessageMiddleware>` z DI scope i wstawiać je do łańcucha middleware PRZED RetryMiddleware. Dotyczy dwóch samples: `TransactionalOutbox` i `InboxDeduplication`.
  -> `src/BareWire/Bus/ReceiveEndpointRunner.cs` (linia 88–112: budowanie middleware chain)
  -> `src/BareWire.Outbox.EntityFramework/ServiceCollectionExtensions.cs` (linia 66: rejestracja IMessageMiddleware)
  -> `src/BareWire.Outbox.EntityFramework/TransactionalOutboxMiddleware.cs`

- [x] **10.8. TransactionalOutbox — TransferConsumer nie wywołuje SaveChangesAsync na TransferDbContext** `[bug]` `[no-test]`
  `TransferConsumer.ConsumeAsync()` ustawia `transfer.Status = "Completed"` (linia 43), ale NIE wywołuje `dbContext.SaveChangesAsync()`. Komentarz w kodzie zakłada, że `TransactionalOutboxMiddleware` obsłuży zapis via `TransactionScope`, ale middleware tylko wywołuje `_dbContext.SaveChangesAsync()` na `OutboxDbContext` (linia 85 middleware), nie na `TransferDbContext`. Każdy DbContext wymaga własnego `SaveChangesAsync()` do flushu zmian — `TransactionScope` gwarantuje atomowość, ale nie automatyczny flush. Dla porównania: `EmailNotificationConsumer` (InboxDeduplication sample) poprawnie wywołuje `SaveChangesAsync()` na swoim DbContext (linia 40). Obserwowane: transfer status zawsze „Pending", nigdy „Completed". Fix: dodać `await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false)` po ustawieniu statusu w `TransferConsumer`.
  -> `samples/BareWire.Samples.TransactionalOutbox/Consumers/TransferConsumer.cs` (linia 43: brak SaveChangesAsync)
  -> `src/BareWire.Outbox.EntityFramework/TransactionalOutboxMiddleware.cs` (linia 85: SaveChangesAsync tylko na OutboxDbContext)

- [x] **10.9. InboxDeduplication — endpointy /duplicate i /redeliver generują nowy MessageId** `[bug]` `[no-test]`
  Endpointy `POST /payments/duplicate` i `POST /payments/redeliver` tworzą nowe wiadomości via `PublishAsync()`, które automatycznie dostają nowy `MessageId` (Guid). Inbox deduplication działa na kluczu `(MessageId, ConsumerType)` — ponieważ MessageId jest inny przy każdym publishu, inbox traktuje każdą wiadomość jako nową. Obserwowane: po 3 publishach (initial + duplicate + redeliver) tabela `NotificationLogs` zawiera 6 rekordów (3×Email + 3×Audit) zamiast oczekiwanych 2 (1×Email + 1×Audit). Aby demonstrować inbox dedup, endpointy /duplicate i /redeliver powinny ustawiać ten sam `MessageId` co oryginalna wiadomość. Wymaga rozszerzenia API `PublishAsync` o opcję przekazania explicit `MessageId` header, lub użycia overloadu z custom headers. Uwaga: ten bug jest blokowany przez 10.7 — nawet z poprawnym MessageId, inbox nie zadziała dopóki middleware nie wejdzie do pipeline.
  -> `samples/BareWire.Samples.InboxDeduplication/Program.cs` (linia 215: PublishAsync bez explicit MessageId)
  -> `src/BareWire.Abstractions/IPublishEndpoint.cs` (brak overloadu z custom MessageId)

### Runda 4 — 2026-03-27 (testy E2E pełnego zestawu 11 samples via Aspire AppHost)

- [x] **10.10. TransactionalOutboxMiddleware — consumerType pochodzi z BW-MessageType zamiast z endpointu** `[bug]` `[critical]`
  `TransactionalOutboxMiddleware.InvokeAsync()` (linia 46) wyprowadza `consumerType` z headera `BW-MessageType`, który zawiera **typ wiadomości** (np. `PaymentReceived`), a nie identyfikator endpointu/konsumenta. Skutek: gdy wielu konsumentów subskrybuje ten sam typ wiadomości na różnych kolejkach (np. `EmailNotificationConsumer` na `payment-email-notifications` i `AuditLogConsumer` na `payment-audit-log`), obaj dostają identyczny klucz kompozytowy inbox `(MessageId, "PaymentReceived")`. Tylko pierwszy konsument przetworzy wiadomość; drugi zostanie błędnie odrzucony jako duplikat. Obserwowane w InboxDeduplication sample: tabela `InboxMessages` zawiera 1 wpis z `ConsumerType = "PaymentReceived"` zamiast oczekiwanych 2 wpisów z różnymi typami konsumentów. Fix wymaga: (1) dodania `EndpointName` do `MessageContext` (lub przekazania go inną drogą), (2) użycia `EndpointName` (nazwa kolejki) jako `consumerType` w middleware zamiast `BW-MessageType`. Alternatywnie: convention `{EndpointName}:{MessageType}` dla klucza kompozytowego.
  -> `src/BareWire.Outbox.EntityFramework/TransactionalOutboxMiddleware.cs` (linia 46: `consumerType` z headera `BW-MessageType`)
  -> `src/BareWire.Abstractions/Pipeline/MessageContext.cs` (brak property `EndpointName`)
  -> `src/BareWire/Bus/ReceiveEndpointRunner.cs` (zna `EndpointName` z `EndpointBinding`, ale nie przekazuje do `MessageContext`)

- [x] **10.11. Inbox lock nigdy nie ustawia ProcessedAt — pozwala na ponowne przetworzenie po ExpiresAt** `[bug]`
  `EfCoreInboxStore.TryLockAsync()` tworzy wpis `InboxMessage` z `ReceivedAt` i `ExpiresAt`, ale nigdy nie ustawia `ProcessedAt`. Po pomyślnym zakończeniu przetwarzania (po `scope.Complete()` w middleware), wpis inbox powinien być oznaczony jako trwale przetworzony (`ProcessedAt = DateTimeOffset.UtcNow`). Obecnie, po upływie `ExpiresAt` (np. 10-20s), `TryLockAsync` traktuje expired lock jako wolny i pozwala na ponowne przetworzenie tego samego messageId — narusza semantykę exactly-once. Obserwowane: po 20s od pierwszego przetworzenia, kolejna próba (redelivery/duplicate) przechodzi przez inbox. Fix: middleware powinien po udanym `scope.Complete()` wywołać `_inboxFilter.MarkProcessedAsync(messageId, consumerType)`, a `TryLockAsync` powinien nigdy nie re-lockować wpisów z `ProcessedAt != null`.
  -> `src/BareWire.Outbox.EntityFramework/EfCoreInboxStore.cs` (linia 26-31: brak `ProcessedAt`, linia 61-69: re-lock expired entries)
  -> `src/BareWire.Outbox.EntityFramework/TransactionalOutboxMiddleware.cs` (linia 88: po `scope.Complete()` brak wywołania `MarkProcessedAsync`)
  -> `src/BareWire.Outbox.EntityFramework/InboxMessage.cs` (`ProcessedAt` zawsze null)

- [x] **10.12. InboxDeduplication sample — /duplicate i /redeliver hardkodują amount 99.99m** `[bug]` `[no-test]`
  Endpointy `/duplicate` i `/redeliver` w `InboxDeduplication` sample tworzą nową instancję `PaymentReceived` z hardkodowaną kwotą `99.99m` (linie 227, 254) zamiast przekazywać oryginalną kwotę. Powoduje to mylące wyniki: logi notification pokazują różne kwoty ($100.00 vs $99.99) dla tego samego paymentId, sugerując błąd w deduplication zamiast poprawnego zachowania. Fix: endpoint powinien przyjmować `amount` jako parametr query lub przechowywać original amount w stanie.
  -> `samples/BareWire.Samples.InboxDeduplication/Program.cs` (linia 227: `99.99m` w /duplicate, linia 254: `99.99m` w /redeliver)

### Runda 5 — 2026-03-27 (pełne E2E testy 11 samples razem via Aspire AppHost)

- [x] **10.13. SagaOrderFlow — payment timeout nigdy nie odpala (ScheduleTimeout to martwy kod)** `[bug]` `[critical]`
  `DelayRequeueScheduleProvider.ScheduleAsync()` (linia 45) to **no-op stub** — zawiera tylko `await Task.CompletedTask` z komentarzem TODO. Metoda powinna tworzyć delay queue z `x-message-ttl` i `x-dead-letter-exchange` wskazującym na destination queue, a następnie publikować wiadomość. Skutek: `ScheduleTimeout<PaymentTimeout>()` zdefiniowany w `OrderSagaStateMachine` (linia 91) rejestruje timeout w `BehaviorContext.ScheduledTimeouts`, `StateMachineExecutor` (linia 104-107) wywołuje `ScheduleAsync` na providerze, ale wiadomość nigdy nie trafia do brokera. Obserwowane: saga zamówienia pozostaje w stanie „Processing" na zawsze (potwierdzono po 65+ sekundach zamiast oczekiwanych 30s). Dotyczy każdej sagi używającej `Schedule<T>()` z dowolną strategią (`Auto` resoluje do `DelayRequeue`). Fix wymaga: implementacja ciała `ScheduleAsync` — (1) deklaracja delay queue `barewire.delay.{ttlMs}.{destinationQueue}` z argumentami `x-message-ttl` i `x-dead-letter-exchange`/`x-dead-letter-routing-key`, (2) serializacja i publikacja wiadomości do delay queue, (3) broker automatycznie dead-letteruje wiadomość do destination queue po upływie TTL.
  -> `src/BareWire.Saga/Scheduling/DelayRequeueScheduleProvider.cs` (linia 43-46: `ScheduleAsync` — TODO stub)
  -> `src/BareWire.Saga/StateMachineExecutor.cs` (linia 104-107: wywołanie `ScheduleAsync`)
  -> `samples/BareWire.Samples.SagaOrderFlow/Saga/OrderSagaStateMachine.cs` (linia 61-65: `Schedule<PaymentTimeout>`, linia 91: `.ScheduleTimeout<>()`)

- [x] **10.14. ObservabilityShowcase — 3-hop distributed trace chain nie działa (topic routing mismatch)** `[bug]`
  Exchange `demo.events` jest typu topic z bindingami: `demo-orders` → `order.*`, `demo-payments` → `payment.*`, `demo-shipments` → `shipment.*`, `demo-saga` → `#`. BareWire publikuje z routing key = pełna nazwa CLR typu (np. `BareWire.Samples.ObservabilityShowcase.Messages.DemoOrderCreated`), który **nie pasuje** do wzorca `order.*`. Skutek: wiadomości trafiają wyłącznie do `demo-saga` (catch-all `#`), kolejki `demo-orders`, `demo-payments` i `demo-shipments` otrzymują 0 wiadomości. `DemoOrderConsumer` nigdy nie przetwarza `DemoOrderCreated`, więc `DemoPaymentProcessed` nigdy nie jest publikowany, i cały 3-hop trace chain (order → payment → shipment) nie wykonuje się. Obserwowane w RabbitMQ management: `demo-saga published=2, delivered=2`; `demo-orders published=0, delivered=0`; analogicznie `demo-payments` i `demo-shipments`. Fix wymaga jednego z: (a) zmiana exchange na `fanout` zamiast `topic` (najprostsze — routing key jest ignorowany), (b) dodanie wsparcia w `PublishAsync` dla explicit routing key (np. overload lub konfiguracja per-message-type → routing-key mapping), (c) zmiana binding patterns na `#` dla wszystkich kolejek (traci sens topic exchange). Opcja (a) jest rekomendowana dla tego sample — topic exchange nie wnosi wartości gdy wszystkie consumery mają przetwarzać wszystkie wiadomości. W przyszłości (b) powinno być zaimplementowane w core jako feature.
  -> `samples/BareWire.Samples.ObservabilityShowcase/Program.cs` (linia 108-124: topology z topic exchange i wzorcami `order.*`, `payment.*`, `shipment.*`)
  -> `src/BareWire/Bus/BareWireBus.cs` (linia 87: `routingKey = typeof(T).FullName` — niezgodne z topic patterns)

- [x] **10.15. Cross-contamination: SagaOrderFlow i RabbitMQ sample współdzielą exchange order.events** `[bug]`
  Oba sample'e deklarują fanout exchange `order.events`: SagaOrderFlow binduje do `order-saga` queue, RabbitMQ sample binduje do `orders` queue. Ponieważ exchange jest fanout, **każda** wiadomość opublikowana przez którykolwiek sample trafia do obu kolejek. Obserwowane: po utworzeniu 3 zamówień w SagaOrderFlow i 1 w RabbitMQ sample, queue `orders` otrzymało 8 wiadomości (4 OrderCreated + 4 inne eventy z SagaOrderFlow). Skutek: (a) `OrderConsumer` w RabbitMQ sample przetwarza eventy z SagaOrderFlow (np. `PaymentReceived`, `ShipmentDispatched`) — deserializacja raw JSON może się udać z domyślnymi wartościami pól, generując fantomowe `OrderProcessed`, (b) saga w SagaOrderFlow otrzymuje eventy z RabbitMQ sample. Fix: zmiana nazwy exchange w jednym z sample'ów — np. RabbitMQ sample powinien używać `rmq-sample.order.events` zamiast `order.events`, oddzielając topologię od SagaOrderFlow.
  -> `samples/BareWire.Samples.RabbitMQ/Program.cs` (linia 74: `rmq.DefaultExchange("order.events")`, linia 86: `DeclareExchange("order.events")`)
  -> `samples/BareWire.Samples.SagaOrderFlow/Program.cs` (linia 90: `rmq.DefaultExchange("order.events")`, linia 97: `DeclareExchange("order.events")`)

- [x] **10.16. InboxDeduplication — komentarz w nagłówku niezgodny z sygnaturą endpointów /duplicate i /redeliver** `[bug]` `[no-test]`
  Komentarz w nagłówku pliku (linia 25) opisuje URL jako `POST /payments/duplicate?paymentId=...`, a endpoint wymaga trzech parametrów: `paymentId`, `messageId` i `amount` (linia 218-221). Parametr `amount` został dodany w ramach fix 10.12, ale komentarz nie został zaktualizowany. Przy wywołaniu bez `amount` endpoint rzuca `BadHttpRequestException: Required parameter "decimal amount" was not provided from query string.` — co jest mylące dla użytkownika testującego sample. Odpowiedź z `POST /payments` powinna zawierać hint o wymaganych parametrach `amount` w URL /duplicate i /redeliver, a komentarz w nagłówku powinien dokumentować pełną sygnaturę.
  -> `samples/BareWire.Samples.InboxDeduplication/Program.cs` (linia 25: komentarz, linia 218-221: sygnatura endpointu)

### Runda 6 — 2026-03-27 (pełne E2E testy 11 samples razem via Aspire AppHost)

- [x] **10.17. SagaOrderFlow — ScheduleTimeout<T> public API zawsze używa TimeSpan.Zero zamiast delay ze Schedule<T>** `[bug]` `[critical]`
  `OrderSagaStateMachine` (linia 61-65) tworzy `paymentTimeoutSchedule` via `Schedule<PaymentTimeout>(cfg => { cfg.Delay = TimeSpan.FromSeconds(30); })`, ale `ScheduleTimeout<PaymentTimeout>((saga, evt) => ...)` na liniach 91 używa publicznego overloadu `IEventActivityBuilder.ScheduleTimeout<T>(factory)`, który w implementacji `EventActivityBuilder` (linia 68-74) tworzy **nowy** `ScheduleHandle<T>(TimeSpan.Zero, SchedulingStrategy.Auto)` — ignorując całkowicie `paymentTimeoutSchedule`. Overload przyjmujący `ScheduleHandle<T>` istnieje w `EventActivityBuilder` (linia 50-58), ale jest oznaczony jako `internal` — sample'e (i cały publiczny API) nie mogą go użyć. Skutek: delay queue `barewire.delay.0.order-saga` jest tworzona z `x-message-ttl = 0`, wiadomość `PaymentTimeout` jest natychmiast dead-letterowana do `order-saga` i saga przechodzi do stanu `Compensating` w ~12ms zamiast po 30 sekundach. Obserwowane: `createdAt` i `updatedAt` sagi różnią się o ~10ms; queue `barewire.delay.0.order-saga` w RabbitMQ management potwierdza TTL=0. Fix wymaga dwóch zmian: (1) dodać overload `ScheduleTimeout<T>(Func<TSaga, TEvent, T> factory, ScheduleHandle<T> schedule)` do publicznego interfejsu `IEventActivityBuilder<TSaga, TEvent>` w `BareWire.Abstractions`, (2) zmienić wywołanie w `OrderSagaStateMachine` na `.ScheduleTimeout<PaymentTimeout>((saga, evt) => new PaymentTimeout(evt.OrderId), paymentTimeoutSchedule)`. Alternatywnie: zmienić publiczny overload bez `ScheduleHandle` tak, aby szukał zarejestrowanego `Schedule<T>` w definicji state machine i używał jego delay/strategy zamiast hardkodowanego `TimeSpan.Zero`.
  -> `src/BareWire.Abstractions/Saga/IEventActivityBuilder.cs` (linia 56: brak overloadu z `ScheduleHandle`)
  -> `src/BareWire.Saga/EventActivityBuilder.cs` (linia 68-74: public overload z `TimeSpan.Zero`, linia 50-58: internal overload z handle)
  -> `samples/BareWire.Samples.SagaOrderFlow/Saga/OrderSagaStateMachine.cs` (linia 61-65: `Schedule<PaymentTimeout>`, linia 91: `.ScheduleTimeout<>()` — handle nie jest przekazywany)

### Runda 7 — 2026-03-27 (pełne E2E testy 11 samples razem via docker-compose)

- [x] **10.18. MultiConsumerPartitioning — consumer dispatch ignoruje BW-MessageType, pierwszy consumer wygrywa** `[bug]` `[critical]` `[unit]`
  `ReceiveEndpointRunner.DispatchMessageAsync()` (linia 312-343) iteruje po invokerach typowanych consumerów sekwencyjnie i polega na wyjątku `UnknownPayloadException`/`BareWireSerializationException` do przejścia do następnego consumera. Problem: `System.Text.Json` z domyślnymi ustawieniami **pomyślnie** deserializuje JSON `PaymentEvent` (`{"PaymentId":"PAY-001","CorrelationId":"...","CreatedAt":"..."}`) jako `OrderEvent` — brakujące pole `OrderId` ustawiane na `null`, istniejące pola `CorrelationId` i `CreatedAt` mapowane poprawnie. Deserializacja nie rzuca wyjątku, więc pierwszy invoker (`OrderEventConsumer`) wygrywa dla **wszystkich** typów wiadomości. Obserwowane: 1000 opublikowanych eventów (333 OrderEvent, 333 PaymentEvent, 334 ShipmentEvent), ale 1000/1000 przetworzonych przez `OrderEventConsumer` z `OrderId=(null)` dla 666 wiadomości. `PaymentEventConsumer` i `ShipmentEventConsumer` nigdy nie są wywoływane (0 logów). Fix wymaga: (1) przed próbą deserializacji sprawdzić header `BW-MessageType` i dopasować go do `MessageType.Name` zarejestrowanego consumera, (2) jeśli header pasuje — wywołać invoker, (3) jeśli header nie pasuje — pominąć invoker BEZ próby deserializacji, (4) fallback na obecną logikę deserializacji-first tylko gdy brak headera `BW-MessageType` (scenariusz raw interop / legacy messages).
  -> `src/BareWire/Bus/ReceiveEndpointRunner.cs` (linia 312-343: dispatch loop — brak sprawdzenia `BW-MessageType`)
  -> `src/BareWire/Bus/BareWireBus.cs` (linia 93, 115: ustawia `BW-MessageType = typeof(T).Name`)
  -> `src/BareWire/Bus/ConsumerInvokerFactory.cs` (invoker nie waliduje typu wiadomości przed deserializacją)

- [x] **10.19. ObservabilityShowcase — demo-saga otrzymuje nieobsługiwane wiadomości (fanout oversharing)** `[bug]` `[no-test]`
  Exchange `demo.events` jest typu fanout — **każda** opublikowana wiadomość trafia do **wszystkich** zbindowanych kolejek: `demo-orders`, `demo-payments`, `demo-shipments` i `demo-saga`. Saga `DemoOrderSagaStateMachine` na kolejce `demo-saga` obsługuje tylko `DemoOrderCreated`, ale otrzymuje też `DemoPaymentProcessed` i `DemoShipmentDispatched` — wiadomości, które nie mają handlera w sadze ani w żadnym typed consumer na tym endpoincie. Skutek: przy każdym uruchomieniu `/demo/run` generowane są 2 ostrzeżenia "No consumer matched message ... on endpoint 'demo-saga'" i 2 wiadomości są odrzucane (rejected). Obserwowane: queue `demo-saga` — `Published: 6, Delivered: 6, Acked: 4` po 2 uruchomieniach demo (4 acked = 2× `DemoOrderCreated` przez sagę, 2 rejected per run = 4 łącznie). Fix: (a) zmienić topologię — saga powinna bindować się do osobnego exchange lub do shared exchange z routing key pasującym tylko do `DemoOrderCreated`, (b) alternatywnie: dispatcher powinien ackować (nie rejectować) wiadomości bez matchującego consumera gdy saga jest zarejestrowana na endpoincie — odrzucanie jest zbyt agresywne dla fanout topologii.
  -> `samples/BareWire.Samples.ObservabilityShowcase/Program.cs` (linia ~108-124: topology z fanout exchange, demo-saga bind)
  -> `src/BareWire/Bus/ReceiveEndpointRunner.cs` (linia 206-211: `Dispatched = false` → `Reject`)

- [x] **10.20. InboxDeduplication — zdeduplikowane wiadomości generują fałszywy warning i są rejectowane zamiast ackowane** `[bug]`
  Gdy `TransactionalOutboxMiddleware` inbox filter odrzuca duplikat wiadomości (ten sam `MessageId + ConsumerType`), middleware short-circuituje pipeline i NIE wywołuje `next`. `TerminatorState.Dispatched` pozostaje `false` → `ReceiveEndpointRunner` loguje warning "No consumer matched message ... on endpoint ..." i ustawia `action = SettlementAction.Reject`. Efekty: (1) 2 fałszywe ostrzeżenia per duplikat (jedno na endpoint `payment-email-notifications`, drugie na `payment-audit-log`), (2) wiadomość jest rejectowana zamiast ackowana — przy queue z DLX, duplikaty trafiłyby niepotrzebnie na DLQ, (3) `EfCoreInboxStore.TryLockAsync()` przy próbie INSERT istniejącego klucza kompozytowego rzuca `DbUpdateException` (23505: duplicate key value violates unique constraint "PK_InboxMessages") — dodatkowy szum w logach. Obserwowane: po wysłaniu initial + duplicate + redeliver na każdą z 2 kolejek: `Published: 3, Delivered: 3, Acked: 1` (tylko initial acked, duplikaty rejected). Fix wymaga: (1) inbox middleware powinno ustawiać flagę na `MessageContext` (np. `context.Items["inbox:filtered"] = true`) gdy odrzuca duplikat, (2) `ReceiveEndpointRunner` powinien sprawdzać tę flagę — jeśli ustawiona, ackować wiadomość bez warning, (3) `EfCoreInboxStore.TryLockAsync()` powinien używać upsert pattern (`INSERT ... ON CONFLICT DO NOTHING`) zamiast blind INSERT, eliminując `DbUpdateException` na duplikatach.
  -> `src/BareWire.Outbox.EntityFramework/TransactionalOutboxMiddleware.cs` (short-circuit bez sygnalizacji do runnera)
  -> `src/BareWire/Bus/ReceiveEndpointRunner.cs` (linia 206-211: brak obsługi inbox-filtered messages)
  -> `src/BareWire.Outbox.EntityFramework/EfCoreInboxStore.cs` (`TryLockAsync` — blind INSERT bez `ON CONFLICT`)

- [x] **10.21. E2E-008 RetryAndDlq — flaky test, ~25% szans na false-negative** `[bug]` `[e2e]`
  Test `E2E008_RetryAndDlq_FailedPaymentReachesDlq` jest niestabilny: przechodzi w izolacji (~100%), ale failuje ~67% uruchomień w pełnym suite E2E (14 testów). Przyczyna: probabilistyczny design testu. `PaymentProcessor` ma 70% failure rate, retry policy = 3 retry (4 próby łącznie). Prawdopodobieństwo, że jedna wiadomość trafia do DLQ = 0.7^4 ≈ 24%. Przy 5 wysłanych wiadomościach prawdopodobieństwo, że ŻADNA nie trafi do DLQ = (1-0.24)^5 ≈ 25%. Dodatkowo przy równoległym uruchomieniu wszystkich samples obciążenie RabbitMQ zwiększa czas przetwarzania, co zaostrza problem z 30-sekundowym timeout poll. Fix: (1) zwiększyć liczbę wysyłanych wiadomości z 5 do 20 (P(żadna w DLQ) = 0.76^20 ≈ 0.3%), (2) zwiększyć PollTimeout z 30s do 60s, (3) alternatywnie — użyć deterministycznego mechanizmu failure (np. flaga `forceFailure=true` zamiast `Random.Shared`).
  -> `tests/BareWire.E2ETests/ErrorHandlingTests.cs` (linia 20-45: E2E008)
  -> `samples/BareWire.Samples.RetryAndDlq/Consumers/PaymentProcessor.cs` (linia 26: `Random.Shared.NextDouble() < FailureRate`)

---

## Funkcjonalność 11: Ulepszenia architektury (code review)

> Zmiany zidentyfikowane podczas przeglądu architektury i kodu (2026-03-27).
> Raport: [docs/review-proposed-changes-2026-03-27.md](../review-proposed-changes-2026-03-27.md)
> Priorytet: Async Scope (bug fix) → Named methods (wydajność) → Serializer options (rozszerzalność API).

### Async Scope (bug fix — CreateScope → CreateAsyncScope)

- [x] **11.1. Zamień `CreateScope()` na `CreateAsyncScope()` w `ConsumerInvokerFactory`** `[unit]`
  `ConsumerInvokerFactory.CreateTyped<>()` (linia 91) i `CreateRawTyped<>()` (linia 116) używają `scopeFactory.CreateScope()`, który zwraca `IServiceScope` (sync `Dispose()`). Jeśli scoped serwisy (np. `DbContext`, `IOutboxStore`) implementują `IAsyncDisposable`, synchroniczny `Dispose()` może blokować thread pool lub pomijać async cleanup. Zamienić na `await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope()` — drop-in replacement, `scope.ServiceProvider` działa identycznie.
  -> `src/BareWire/Bus/ConsumerInvokerFactory.cs` (linia 91, 116)

- [x] **11.2. Zamień `CreateScope()` na `CreateAsyncScope()` w `ReceiveEndpointRunner`** `[unit]`
  `ReceiveEndpointRunner.RunAsync()` (linia 171) tworzy `IServiceScope` per message dla `MessageContext` i DI middleware. Zamienić na `await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope()` — zapewnia poprawny async dispose dla scoped serwisów (`OutboxDbContext`, `InboxFilter`, `TransactionalOutboxMiddleware`).
  -> `src/BareWire/Bus/ReceiveEndpointRunner.cs` (linia 171)

- [x] **11.3. Zamień `CreateScope()` na `CreateAsyncScope()` w `ConsumerDispatcher`** `[unit]`
  `ConsumerDispatcher.DispatchAsync<T>()` (linia 22) i `DispatchRawAsync()` (linia 38) używają `CreateScope()` z manualnym `scope.Dispose()` w `finally`. Zamienić na `await using var scope = _scopeFactory.CreateAsyncScope()` i usunąć manual try/finally. UWAGA: zweryfikować czy `ConsumerDispatcher` jest jeszcze używany w bieżącym flow — `ConsumerInvokerFactory` tworzy własny scope (duplikacja).
  -> `src/BareWire/Pipeline/ConsumerDispatcher.cs` (linia 22, 38)

### Named methods zamiast lambd (wydajność + debuggability)

- [x] **11.4. Wyekstrahuj named methods z `ConsumerInvokerFactory` lambd** `[unit]`
  Lambdy w `CreateTyped<>()` (linia 73-94) i `CreateRawTyped<>()` (linia 100-119) pojawiają się jako `<CreateTyped>b__0` w stack traces, profilerach i APM. Zamienić na prywatne statyczne metody generyczne (np. `InvokeTypedConsumerAsync<TConsumer, TMessage>()` i `InvokeRawConsumerAsync<TRawConsumer>()`). Delegat tworzony raz na startup — zmiana głównie dla czytelności stack trace i diagnostyki.
  -> `src/BareWire/Bus/ConsumerInvokerFactory.cs` (linia 73-94, 100-119)

- [x] **11.5. Wyekstrahuj terminator i pipeline builder z `ReceiveEndpointRunner` lambd** `[unit]`
  `ReceiveEndpointRunner.RunAsync()` tworzy per-message lambdy: terminator (linia 181-185) przechwytujący `dispatched`/`messageType`, oraz pipeline composition lambdy (linia 203, 209, 215) przechwytujące `_staticChain`/`terminator`/`captured`. To realne per-message closure allocations na hot path. Wyekstrahować jako named private methods lub nested struct/class z jasną sygnaturą. Cel: eliminacja closure allocation + czytelne stack traces.
  -> `src/BareWire/Bus/ReceiveEndpointRunner.cs` (linia 181-185, 199-219)

### Serializer options (rozszerzalność API)

- [x] **11.6. Dodaj `IDeserializerResolver` do pipeline zamiast bezpośredniego `IMessageDeserializer`** `[unit]`
  `ConsumerInvokerFactory` delegaty (`InvokerDelegate`, `RawInvokerDelegate`) przyjmują `IMessageDeserializer` jako parametr (linia 27, 42). Nie obsługuje to mieszanych content-types na jednym endpoint (np. JSON + Protobuf). Zamienić parametr na `IDeserializerResolver` i resolve'ować deserializer po `content-type` header z wiadomości. `IDeserializerResolver` i `ContentTypeDeserializerRouter` już istnieją w `BareWire.Abstractions.Serialization`. Fallback: jeśli brak `IDeserializerResolver` w DI, użyć domyślnego `IMessageDeserializer` (backwards compatibility).
  -> `src/BareWire/Bus/ConsumerInvokerFactory.cs` (delegaty, linia 27, 42, 75)
  -> `src/BareWire/Bus/ReceiveEndpointRunner.cs` (przekazywanie deserializera do invokerów)
  -> `src/BareWire.Abstractions/Serialization/IDeserializerResolver.cs`

- [x] **11.7. Zarejestruj `ContentTypeDeserializerRouter` jako domyślny `IDeserializerResolver` w DI** `[no-test]`
  `ContentTypeDeserializerRouter` implementuje `IDeserializerResolver`, ale nie jest zarejestrowany w DI. Dodać `TryAddSingleton<IDeserializerResolver, ContentTypeDeserializerRouter>()` do `AddBareWireJsonSerializer()` (lub `AddBareWire()`). Router powinien automatycznie zbierać wszystkie zarejestrowane `IMessageDeserializer` instancje (lub przyjmować je jako constructor dependency).
  -> `src/BareWire.Serialization.Json/ServiceCollectionExtensions.cs`
  -> `src/BareWire.Serialization.Json/ContentTypeDeserializerRouter.cs`
  -> `src/BareWire/ServiceCollectionExtensions.cs`

- [x] **11.8. Dodaj per-endpoint override serializera/deserializera w `IEndpointConfigurator`** `[no-test]`
  Obecnie serializer/deserializer jest globalny (bus-level). Dodać opcję per-endpoint override via fluent API: `e.UseSerializer<ProtobufSerializer>()` i `e.UseDeserializer<ProtobufDeserializer>()` na `IEndpointConfigurator`. Implementacja: `EndpointBinding` przechowuje opcjonalny override type; `ReceiveEndpointRunner` resolve'uje per-endpoint lub fallback na globalny. Zachować singleton lifetime — serializery są stateless.
  -> `src/BareWire.Abstractions/Configuration/IEndpointConfigurator.cs` (nowe metody)
  -> `src/BareWire/Configuration/EndpointBinding.cs` (opcjonalne override types)
  -> `src/BareWire/Bus/ReceiveEndpointRunner.cs` (resolve per-endpoint lub globalny)

---

## Funkcjonalność 12: Interop MassTransit

> Pakiet integracyjny do odbierania wiadomości w formacie koperty MassTransit.
> Kryterium zakończenia: deserializacja koperty MassTransit działa z auto-routingiem po Content-Type.

### Projekt i DTO

- [x] **12.1. Utwórz projekt BareWire.Interop.MassTransit** `[no-test]`
  Nowy projekt `src/BareWire.Interop.MassTransit` z `ProjectReference` do `Abstractions` + `Serialization.Json`. `PackageReference`: `Microsoft.Extensions.DependencyInjection.Abstractions`. `InternalsVisibleTo`: `BareWire.UnitTests`, `BareWire.IntegrationTests`. Dodać do `BareWire.slnx` (folder `/src/`) i `BareWire.UnitTests.csproj`.
  -> [extension-points.md](../architecture/architecture/extension-points.md)

- [x] **12.2. Zaimplementuj MassTransitEnvelope DTO** `[no-test]`
  `internal sealed record MassTransitEnvelope` z polami: `MessageId`, `CorrelationId`, `ConversationId`, `InitiatorId`, `SourceAddress`, `DestinationAddress`, `MessageType`, `SentTime`, `ExpirationTime`, `Headers`, `Message` (`JsonElement`). Wszystkie pola nullable — permisywne parsowanie obcych kopert. Nieznane pola (`host`, `faultAddress`, `requestId`, `responseAddress`) ignorowane przez `System.Text.Json` (domyślne zachowanie).
  -> [migration-guide.md](../architecture/appendix/migration-guide.md)

### Deserializer i DI

- [x] **12.3. Zaimplementuj MassTransitEnvelopeDeserializer** `[unit]`
  `internal sealed class` implementująca `IMessageDeserializer`. `ContentType`: `application/vnd.masstransit+json`. Deserializacja: `Utf8JsonReader` na `ReadOnlySequence<byte>` → `MassTransitEnvelope` → `envelope.Message.Deserialize<T>(BareWireJsonSerializerOptions.Default)`. Wzorzec identyczny jak `BareWireEnvelopeSerializer.Deserialize<T>()`. Zero-copy (ADR-003). `JsonException` → `BareWireSerializationException` z `ExtractRawPayload`.
  -> [extension-points.md](../architecture/architecture/extension-points.md), [ADR-003](../architecture/decisions/ADR-003-zero-copy-pipeline.md)

- [x] **12.4. Zaimplementuj ServiceCollectionExtensions.AddMassTransitEnvelopeDeserializer()** `[unit]`
  Metoda rozszerzenia rejestrująca `MassTransitEnvelopeDeserializer` w DI i zastępująca `IDeserializerResolver` routerem uwzględniającym deserializer MassTransit. Kolejność wywołań: `AddBareWireJsonSerializer()` → `AddMassTransitEnvelopeDeserializer()`. Test DI: resolve `IDeserializerResolver`, routing `application/vnd.masstransit+json` → MassTransit deserializer.
  -> [configuration.md](../architecture/api/configuration.md)

### Testy

- [x] **12.5. Dodaj testy jednostkowe MassTransitEnvelopeDeserializer** `[unit]`
  ~9 testów: valid envelope, empty payload, invalid JSON, all metadata fields, missing optional fields, nested message, null message field, content-type assertion, multi-segment sequence. Reuse: `SimpleMessage`, `NestedMessage`, `InnerData` z istniejących testów.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [x] **12.6. Dodaj test routera dla Content-Type MassTransit** `[unit]`
  W istniejącym `ContentTypeDeserializerRouterTests` dodać test: `Resolve_VndMasstransitJson_ReturnsMassTransitDeserializer`.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

### Sample

- [x] **12.7. Utwórz BareWire.Samples.MassTransitInterop** `[no-test]`
  Sample demonstrująca koegzystencję BareWire z MassTransit. Scenariusz: dwa endpointy — jeden odbiera wiadomości w formacie koperty MassTransit (`application/vnd.masstransit+json`), drugi odbiera raw JSON z BareWire. API: `POST /masstransit/simulate` → publikuje wiadomość w formacie koperty MT na kolejkę, `POST /barewire/publish` → publikuje raw JSON. Consumer loguje otrzymane wiadomości z metadata (`messageId`, `correlationId`). README z opisem scenariusza i konfiguracji. Dodać do `BareWire.slnx` (folder `/samples/`) i `BareWire.Samples.AppHost`.
  -> [migration-guide.md](../architecture/appendix/migration-guide.md)

---

## Macierz pokrycia

### TDD → Zadania

| Sekcja TDD | Pokrycie | Zadania |
|------------|----------|---------|
| 4. Architektura | Tak | Funkcjonalność 0 (structure), 1 (layers) |
| 5. Rdzeń API | Tak | Funkcjonalność 1, zadania 1.1-1.5 |
| 6. Raw-Message Interop | Tak | Funkcjonalność 2, zadania 2.1-2.4 |
| 7. Ręczna topologia | Tak | Funkcjonalność 3, zadania 3.2, 3.8 |
| 8. Adaptery transportowe | Tak (RabbitMQ) | Funkcjonalność 3, zadania 3.1-3.9 |
| 9. Flow Control | Tak | Funkcjonalność 1, zadania 1.7, 1.14 |
| 10. Optymalizacja pamięci | Tak | Funkcjonalność 1, zadanie 1.6 |
| 11. SAGA Engine | Tak | Funkcjonalność 4, zadania 4.1-4.10 |
| 12. Outbox / Inbox | Tak | Funkcjonalność 5, zadania 5.1-5.8 |
| 13. Observability | Tak | Funkcjonalność 6, zadania 6.1-6.7 |
| 14. Bezpieczeństwo | Tak | Funkcjonalność 3 (TLS), 6 (health redaction) |
| 15. Serializacja | Tak | Funkcjonalność 2, zadania 2.1-2.5 |
| 16. Strategia testowania | Tak | Funkcjonalność 7, zadania 7.1-7.8 |
| Samples (wszystkie sekcje) | Tak | Funkcjonalność 8, zadania 8.1-8.11 |

### ADR → Zadania

| ADR | Pokrycie | Zadania |
|-----|----------|---------|
| ADR-001 Raw-first | Tak | 2.1, 2.2, 2.4 (Content-Type routing) |
| ADR-002 Manual topology | Tak | 3.2, 3.8 (ConfigureConsumeTopology = false) |
| ADR-003 Zero-copy pipeline | Tak | 1.6, 2.1, 2.2 (IBufferWriter/ReadOnlySequence) |
| ADR-004 Credit-based flow | Tak | 1.7 (FlowController/CreditManager) |
| ADR-005 MassTransit naming | Tak | 1.1 (IBus, IConsumer<T>, ConsumeContext<T>) |
| ADR-006 Publish backpressure | Tak | 1.14 (bounded outgoing channel) |

### Pakiety → Zadania

| Pakiet | Pokrycie | Funkcjonalności |
|--------|----------|-----------------|
| BareWire.Abstractions | Tak | Funkcjonalność 1 (1.1-1.5) |
| BareWire | Tak | Funkcjonalność 1 (1.6-1.15) |
| BareWire.Serialization.Json | Tak | Funkcjonalność 2 |
| BareWire.Transport.RabbitMQ | Tak | Funkcjonalność 3 |
| BareWire.Saga | Tak | Funkcjonalność 4 (4.1-4.7) |
| BareWire.Saga.EntityFramework | Tak | Funkcjonalność 4 (4.8-4.9) |
| BareWire.Outbox | Tak | Funkcjonalność 5 (5.1-5.4) |
| BareWire.Outbox.EntityFramework | Tak | Funkcjonalność 5 (5.5-5.7) |
| BareWire.Observability | Tak | Funkcjonalność 6 |
| BareWire.Testing | Tak | Funkcjonalność 1 (1.16-1.17) |
| BareWire.Interop.MassTransit | Tak | Funkcjonalność 12 |
| BareWire.Samples.* | Tak | Funkcjonalność 8 (8.1-8.11) |

### Pokrycie testowe

| Typ testu | Liczba | % całości |
|-----------|--------|-----------|
| Unit | 51 | 41% |
| Integration | 17 | 14% |
| E2E | 4 | 3% |
| No-test | 53 | 42% |
