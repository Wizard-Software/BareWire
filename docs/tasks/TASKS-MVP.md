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
| 1. Fundament (Abstractions + Core + Testing) | 20 | 13 | 3 | 0 | 4 |
| 2. Serializacja | 6 | 5 | 0 | 0 | 1 |
| 3. Transport RabbitMQ | 10 | 2 | 7 | 1 | 0 |
| 4. SAGA Engine | 10 | 7 | 1 | 1 | 1 |
| 5. Outbox / Inbox | 8 | 4 | 1 | 1 | 2 |
| 6. Observability | 7 | 3 | 4 | 0 | 0 |
| 7. Kontrakty + Benchmarki + Samples | 8 | 2 | 0 | 1 | 5 |
| **Razem** | **77** | **36** | **16** | **4** | **21** |

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

### BareWire.Core — Bufory i Flow Control

- [x] **1.6. Zaimplementuj MessageRingBuffer<T> i PooledBufferWriter** `[unit]`
  `MessageRingBuffer<T>`: SPSC, power-of-2 capacity, `Volatile.Read/Write`, `TryWrite`/`TryRead`, zero-alloc. `PooledBufferWriter`: `IBufferWriter<byte>` z `ArrayPool<byte>.Shared`, `GetMemory`/`GetSpan`/`Advance`, `Dispose` zwraca bufor. Testy: write/read, pełny/pusty bufor, resize, pool return.
  -> [internal-components.md](../architecture/architecture/internal-components.md), [TDD.md sekcja 10](../TDD.md)

- [x] **1.7. Zaimplementuj FlowController, CreditManager i EndpointPipeline** `[unit]`
  `FlowController`: zarządzanie kredytami per endpoint. `CreditManager`: atomowe `TryGrantCredits`, `TrackInflight`/`ReleaseInflight` (Interlocked). `EndpointPipeline`: `Channel.CreateBounded<InboundMessage>`, inflight tracking (`_inflightCount`, `_inflightBytes`), health alert at 90%. Testy: grant/deny credits, limits, bounded channel backpressure.
  -> [internal-components.md](../architecture/architecture/internal-components.md), [ADR-004](../architecture/decisions/ADR-004-credit-based-flow-control.md)

### BareWire.Core — Pipeline

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

### BareWire.Core — Middleware

- [x] **1.12. Zaimplementuj RetryMiddleware i DeadLetterMiddleware** `[unit]`
  `RetryMiddleware`: strategie Interval, Incremental, Exponential backoff. Filtry: `Handle<TException>`, `Ignore<TException>`. Konfigurowalny max retries. `DeadLetterMiddleware`: routing do DLQ po wyczerpaniu retry. Callback `OnDeadLetter`. Testy: retry count, backoff intervals, exception filtering, DLQ routing.
  -> [error-handling.md](../architecture/api/error-handling.md), [extension-points.md](../architecture/architecture/extension-points.md)

- [x] **1.13. Zaimplementuj PartitionerMiddleware** `[unit]`
  Serializacja przetwarzania per klucz (`CorrelationId` lub custom `Func<ConsumeContext, Guid>`). `SemaphoreSlim(1, 1)` per partycja. Konfigurowalny `partitionCount`. Testy: concurrent messages z tym samym kluczem przetwarzane sekwencyjnie.
  -> [internal-components.md](../architecture/architecture/internal-components.md), [TDD.md sekcja 11.6](../TDD.md)

- [x] **1.14. Zaimplementuj publish-side backpressure (ADR-006)** `[unit]`
  Bounded outgoing channel dla `PublishAsync`. `PublishFlowControlOptions` (MaxPendingPublishes, FullMode). Health alert at 90% capacity. Testy: backpressure activation, health status degradation.
  -> [ADR-006](../architecture/decisions/ADR-006-publish-backpressure.md)

### BareWire.Core — DI i konfiguracja

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

---

## Funkcjonalność 4: SAGA Engine

> Faza 4 z planu implementacji. Wbudowany SAGA state machine z persystencją.
> Kryterium zakończenia: OrderSagaStateMachine działa end-to-end z persystencją EF Core.

### BareWire.Saga

- [ ] **4.1. Zaimplementuj BareWireStateMachine<T> z fluent API** `[unit]`
  Klasa bazowa: `Event<T>()`, `State()`, `Schedule<T>()`. Definicja przejść: `During(state, activities)`, `Initially(activities)`, `DuringAny(activities)`, `Finally(finalizer)`. Korelacja: `CorrelateBy<T>(event, selector)`, `CorrelateBy<T>(event, expression)`. Testy: definicja state machine, przejścia stanów, korelacja.
  -> [TDD.md sekcja 11.2](../TDD.md)

- [ ] **4.2. Zaimplementuj IEventActivityBuilder z akcjami SAGA** `[unit]`
  `TransitionTo(state)`, `Then(action)`, `Publish<T>(factory)`, `Send<T>(destination, factory)`, `ScheduleTimeout(schedule, delay)`, `CancelTimeout(schedule)`, `Finalize()`. Fluent chain. Integracja z outbox (publish/send buforowane). Testy: chain building, each activity type.
  -> [TDD.md sekcja 11.3](../TDD.md)

- [ ] **4.3. Zaimplementuj StateMachineExecutor<T>** `[unit]`
  Execution engine: load state → match event → execute activities → persist state. Optimistic concurrency: retry na `ConcurrencyException`. Testy: state transitions, event mismatch (ignore), concurrent modification.
  -> [TDD.md sekcja 11.1](../TDD.md)

- [ ] **4.4. Zaimplementuj SagaEventRouter i CorrelationProvider** `[unit]`
  `SagaEventRouter`: routing eventów do instancji SAGA po korelacji. `CorrelationProvider`: `CorrelateById` (Guid z payloadu), `CorrelateByExpression` (dla queryable repo). SAGA z raw events: korelacja po polu z JSON payloadu. Testy: routing po CorrelationId, expression correlation, missing correlation → error.
  -> [TDD.md sekcja 11.2, 11.7](../TDD.md)

- [ ] **4.5. Zaimplementuj strategie schedulingu timeoutów** `[unit]`
  `IScheduleConfigurator` z `Delay` i `Strategy`. Strategie: `Auto` (wybiera najlepszą), `DelayRequeue` (TTL + DLX na dedykowaną delay queue — bez pluginu), `TransportNative` (ASB), `ExternalScheduler` (Quartz/Hangfire), `DelayTopic` (Kafka). Dla MVP: implementacja `DelayRequeue` dla RabbitMQ. Testy: Auto selection, delay requeue flow.
  -> [TDD.md sekcja 11.4](../TDD.md)

- [ ] **4.6. Zaimplementuj RoutingSlipExecutor i ICompensableActivity** `[unit]`
  `ICompensableActivity<TArguments, TLog>`: `ExecuteAsync` (forward), `CompensateAsync` (rollback). `RoutingSlipExecutor`: execute chain of activities; on failure → compensate in reverse. `CompensationLog` persistence. Testy: happy path, failure + compensation, partial failure.
  -> [TDD.md sekcja 11.8](../TDD.md)

- [ ] **4.7. Zaimplementuj InMemorySagaRepository<T>** `[unit]`
  `ISagaRepository<T>` z `ConcurrentDictionary<Guid, TSaga>`. Optimistic concurrency per key (Version check). Do testów/dev (volatile). Testy: CRUD, concurrency conflict, delete.
  -> [TDD.md sekcja 11.5](../TDD.md)

### BareWire.Saga.EntityFramework

- [ ] **4.8. Zaimplementuj EfCoreSagaRepository<T> z optimistic concurrency** `[integration]`
  `ISagaRepository<T>` i `IQueryableSagaRepository<T>` z EF Core 10. Optimistic concurrency: `Version` property mapowany na `RowVersion` (SQL Server) lub `xmin` (PostgreSQL). `FindAsync`, `SaveAsync`, `UpdateAsync` z concurrency check. `FindSingleAsync` z expression predicate. Test: CRUD, concurrency conflict → `ConcurrencyException`.
  -> [TDD.md sekcja 11.5](../TDD.md)

- [ ] **4.9. Utwórz SagaDbContext z migracjami EF Core** `[no-test]`
  `SagaDbContext` z konfigurowanym mapowaniem TSaga. Indeks na `CorrelationId`. Concurrency token na `Version`. Migracja initial create. Konfiguracja per-saga registration.
  -> [TDD.md sekcja 11.5](../TDD.md)

### Testy SAGA

- [ ] **4.10. Dodaj testy integracyjne: SAGA E2E z RabbitMQ + EF Core** `[e2e]`
  Scenariusz OrderSaga: (1) OrderCreated → state: Processing, (2) PaymentReceived → state: Completed + publish OrderCompleted, (3) PaymentFailed → state: Compensating → Failed. Concurrency: 10 instancji, 100 eventów per instancja, < 5% conflicts. Timeout: Schedule → fire after delay. Kompensacja: RoutingSlip 3 activities → failure → compensate.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

---

## Funkcjonalność 5: Outbox / Inbox

> Faza 5 z planu implementacji. Gwarancje exactly-once w pipeline.
> Kryterium zakończenia: transactional outbox — business state + outbox w jednej transakcji DB.

### BareWire.Outbox

- [ ] **5.1. Zaimplementuj InMemoryOutboxMiddleware** `[unit]`
  `IMessageMiddleware`: buforowanie `PublishAsync`/`SendAsync` w pamięci. Po sukcesie handlera: `FlushAsync()` → `SendBatch` do adaptera → ack original. Przy wyjątku: `Discard()` bufor → nack/abandon original. Testy: buffer + flush, discard on error, nested publish.
  -> [TDD.md sekcja 12.1](../TDD.md)

- [ ] **5.2. Zaimplementuj OutboxDispatcher (background polling service)** `[unit]`
  Background `IHostedService`: polling loop (konfigurowalny interval, domyślnie 1s). `SELECT` pending outbox messages (batch size konfigurowalny, domyślnie 100). Publish → update status = delivered. Idempotent: skip already delivered. Testy: polling, batch dispatch, idempotency.
  -> [TDD.md sekcja 12.2](../TDD.md)

- [ ] **5.3. Zaimplementuj InboxFilter (deduplication)** `[unit]`
  `TryLock(MessageId)`: sprawdzenie czy wiadomość była już przetworzona. Already-processed → ack (idempotent skip). Lock timeout (konfigurowalny, domyślnie 30s). Testy: first process → lock, duplicate → skip, expired lock.
  -> [TDD.md sekcja 12.2](../TDD.md)

- [ ] **5.4. Zaimplementuj konfigurację retention i cleanup** `[unit]`
  `IOutboxConfigurator`: `InboxRetention` (domyślnie 7 dni), `OutboxRetention` (domyślnie 7 dni), `PollingInterval`, `DispatchBatchSize`, `InboxLockTimeout`. Background cleanup job: usuwanie rekordów starszych niż retention. Testy: retention calculation, cleanup logic.
  -> [TDD.md sekcja 12.3](../TDD.md)

### BareWire.Outbox.EntityFramework

- [ ] **5.5. Utwórz entity OutboxMessage i InboxMessage z migracjami EF Core** `[no-test]`
  `OutboxMessage`: Id (PK auto), MessageId (Guid, indexed), Body (byte[]), ContentType, DestinationAddress, Headers (JSON), Status (Pending/Delivered), CreatedAt, DeliveredAt. `InboxMessage`: Id (PK auto), MessageId (Guid, unique indexed), ProcessedAt, ExpiresAt. Migracja initial create z indeksami.
  -> [TDD.md sekcja 12.4](../TDD.md)

- [ ] **5.6. Zaimplementuj TransactionalOutboxMiddleware** `[integration]`
  `IMessageMiddleware`: inbox check (TryLock MessageId) → handler execution → business state + outbox messages w jednej transakcji EF Core `SaveChangesAsync()` → ack original. Rollback on error. Testy: exactly-once (business + outbox atomic), inbox dedup, rollback.
  -> [TDD.md sekcja 12.2](../TDD.md)

- [ ] **5.7. Utwórz OutboxDbContext** `[no-test]`
  DbContext z DbSet<OutboxMessage>, DbSet<InboxMessage>. Konfiguracja mapowania, indeksów. Rejestracja w DI z konfiguracją connection string.
  -> [TDD.md sekcja 12.4](../TDD.md)

### Testy Outbox

- [ ] **5.8. Dodaj testy integracyjne: transactional outbox z EF Core + RabbitMQ** `[e2e]`
  Scenariusze: (1) Happy path: consume → business save + outbox → dispatcher delivers, (2) Inbox dedup: ten sam MessageId dwa razy → przetworzenie 1 raz, (3) Handler failure → rollback (brak outbox records), (4) Dispatcher retry: broker down → pending → broker up → delivered. (5) Retention cleanup: stare records usunięte.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

---

## Funkcjonalność 6: Observability

> Faza 6 z planu implementacji. OpenTelemetry-first tracing + metrics + health checks.
> Kryterium zakończenia: spany widoczne w Aspire Dashboard; metryki w OTel.

- [ ] **6.1. Zaimplementuj BareWireActivitySource (OTel tracing)** `[unit]`
  `ActivitySource("BareWire")`: spany publish (producer), consume (consumer), saga.transition, outbox.dispatch. Tags: `messaging.system`, `messaging.destination`, `messaging.message_id`, `messaging.correlation_id`. Kind: Producer/Consumer. Testy: span creation, tag values, parent-child relationship.
  -> [TDD.md sekcja 13](../TDD.md)

- [ ] **6.2. Zaimplementuj BareWireMetrics (OTel counters i histogramy)** `[unit]`
  `Meter("BareWire")`: Counters: `barewire.messages.published`, `barewire.messages.consumed`, `barewire.messages.failed`, `barewire.messages.dead_lettered`. Histogramy: `barewire.message.duration` (processing time), `barewire.message.size` (payload bytes). Tags: endpoint, message_type. Testy: counter increment, histogram record.
  -> [TDD.md sekcja 13](../TDD.md)

- [ ] **6.3. Zaimplementuj propagację trace context (traceparent header)** `[integration]`
  Publish: inject `Activity.Current` → header `traceparent` w transport headers. Consume: extract `traceparent` z headers → create child span z propagowanym trace context. W3C TraceContext format. Test: publish → consume → verify trace_id propagation.
  -> [TDD.md sekcja 13](../TDD.md), [security-architecture.md](../architecture/security/security-architecture.md)

- [ ] **6.4. Zaimplementuj BareWireEventCounterSource** `[unit]`
  `EventSource("BareWire")` z `EventCounters`: inflight-messages, channel-utilization, publish-rate, consume-rate, alloc-rate. Konfigurowalny interval (domyślnie 5s). Low-overhead mode (< 1% narzutu). Testy: counter publishing, interval.
  -> [TDD.md sekcja 13](../TDD.md)

- [ ] **6.5. Zaimplementuj BareWireHealthCheck** `[integration]`
  `IHealthCheck`: broker connection status, inflight load (> 90% → Degraded), outbox pending count (> threshold → Degraded), saga stuck detection (state unchanged > timeout → Degraded). Redacted connection strings w output (SEC-06). Test: healthy, degraded (high inflight), unhealthy (broker down).
  -> [TDD.md sekcja 13](../TDD.md), [security-architecture.md](../architecture/security/security-architecture.md)

- [ ] **6.6. Dodaj integrację z Aspire Dashboard** `[integration]`
  `ConfigureObservability(Action<IObservabilityConfigurator>)`: `EnableOpenTelemetry` (default: true), `EnableEventCounters` (default: true). Konfiguracja OTel exporter (OTLP). Weryfikacja spanów i metryk w Aspire Dashboard. Test: spany publish/consume widoczne w dashboardzie.
  -> [TDD.md sekcja 13](../TDD.md)

- [ ] **6.7. Dodaj testy integracyjne observability** `[integration]`
  Pokrycie: (1) spany publish → consume z poprawnym trace_id, (2) metryki: counter increment po publish/consume, (3) health check: healthy/degraded/unhealthy, (4) EventCounters: inflight tracking. In-process OTel collector w testach.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

---

## Funkcjonalność 7: Kontrakty + Benchmarki + Samples

> Faza 7 z planu implementacji. Stabilność API, performance validation, dokumentacja samples.
> Kryterium zakończenia: wszystkie testy przechodzą; benchmarki spełniają target; sample działa.

### Contract Tests

- [ ] **7.1. Zaimplementuj PublicApiTests z PublicApiGenerator** `[unit]`
  Snapshot publicznego API: `BareWire.Abstractions`, `BareWire.Core` (publiczne extension methods). Porównanie z `.approved.txt`. Fail na breaking change (usunięcie/zmiana sygnatury publicznej metody). Test: generate → compare → fail on diff.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [ ] **7.2. Zaimplementuj ArchitectureRuleTests z NetArchTest** `[unit]`
  Reguły: Transport !→ Core, Observability !→ Core, Abstractions !→ {Core, Transport, Observability, Saga, Outbox}, Serialization !→ {Core, Transport}, Saga → {Abstractions, Core} only. Test per reguła z `Types.InAssembly().ShouldNot().HaveDependencyOn()`.
  -> [CONSTITUTION.md](../architecture/CONSTITUTION.md), [testing-spec.md](../architecture/testing/testing-spec.md)

- [ ] **7.3. Wygeneruj .approved.txt dla wszystkich publicznych pakietów** `[no-test]`
  `BareWire.Abstractions.approved.txt`, `BareWire.Core.approved.txt` (extension methods). Baseline dla przyszłych breaking change checks. Commit do repo.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

### Benchmarki

- [ ] **7.4. Dodaj pełny benchmark suite** `[no-test]`
  `PublishBenchmarks`: typowany in-memory, raw in-memory, typowany RabbitMQ. `ConsumeBenchmarks`: consume + ack in-memory, consume + ack RabbitMQ. `SerializationBenchmarks`: serialize/deserialize 100 B — 100 KB. `SagaBenchmarks`: state transition in-memory. Wszystkie z `[MemoryDiagnoser]` i `[EventPipeProfiler]`.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

- [ ] **7.5. Zwaliduj performance targets vs baseline** `[no-test]`
  Publish typowany: > 500K msgs/s, < 128 B/msg. Publish raw: > 1M msgs/s, 0 B/msg. Consume + ack: > 300K msgs/s, < 256 B/msg. SAGA transition: > 100K msgs/s, < 512 B/msg. JSON serialize 1 KB: < 1 μs, < 128 B. JSON deserialize 1 KB: < 1 μs, < 256 B.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

### Samples i CI

- [ ] **7.6. Utwórz BareWire.Samples.RabbitMQ** `[no-test]`
  Przykładowa aplikacja ASP.NET Core: POST /orders → publish OrderCreated. Consumer → process → publish OrderProcessed. SAGA: OrderSaga z tranzycjami. Outbox: transactional outbox z EF Core. Config: RabbitMQ + Aspire + health checks + OTel.
  -> [usage/getting-started.md](../architecture/usage/getting-started.md)

- [ ] **7.7. Rozszerz CI pipeline o contract check i benchmark gate** `[no-test]`
  GitHub Actions: contract test failure → block merge. Benchmark regresja > 10% alokacji → warning (nie block w MVP). Artifact: benchmark results jako CI artifact.
  -> [implementation-plan.md](../architecture/implementation-plan.md)

### Scenariusze E2E

- [ ] **7.8. Dodaj obowiązkowe scenariusze E2E z testing-spec.md** `[e2e]`
  E2E-1: steady-state throughput 10 min (< 256 B/msg, 0 GC Gen2). E2E-2: spike 10x → backpressure → recovery < 30s. E2E-3: retry storm + outbox/inbox (50% failures) → exactly-once. E2E-4: SAGA concurrency (1000 events → 10 instancji, < 5% conflicts). E2E-5: large payloads > 256 KB.
  -> [testing-spec.md](../architecture/testing/testing-spec.md)

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
| BareWire.Core | Tak | Funkcjonalność 1 (1.6-1.15) |
| BareWire.Serialization.Json | Tak | Funkcjonalność 2 |
| BareWire.Transport.RabbitMQ | Tak | Funkcjonalność 3 |
| BareWire.Saga | Tak | Funkcjonalność 4 (4.1-4.7) |
| BareWire.Saga.EntityFramework | Tak | Funkcjonalność 4 (4.8-4.9) |
| BareWire.Outbox | Tak | Funkcjonalność 5 (5.1-5.4) |
| BareWire.Outbox.EntityFramework | Tak | Funkcjonalność 5 (5.5-5.7) |
| BareWire.Observability | Tak | Funkcjonalność 6 |
| BareWire.Testing | Tak | Funkcjonalność 1 (1.16-1.17) |

### Pokrycie testowe

| Typ testu | Liczba | % całości |
|-----------|--------|-----------|
| Unit | 36 | 47% |
| Integration | 16 | 21% |
| E2E | 4 | 5% |
| No-test | 21 | 27% |
