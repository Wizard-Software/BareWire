# BareWire Samples — Opis workflow krok po kroku

Dokument opisuje każdy przykład (sample) z katalogu `samples/`. Dla każdego projektu podano: cel, demonstrowane funkcjonalności BareWire, architekturę przepływu wiadomości, dokładny opis kroków workflow (jakie zapytania HTTP wykonać i co powinniśmy otrzymać w odpowiedzi), oraz zastosowania w praktyce.

> **Wymagania wstępne (wspólne):** Wszystkie przykłady wymagają działającego brokera RabbitMQ. Część wymaga PostgreSQL. Przy uruchomieniu przez Aspire AppHost (`BareWire.Samples.AppHost`) obie usługi są provisionowane automatycznie z Docker.

---

## Spis treści

1. [BasicPublishConsume — Podstawowy publish/consume](#1-basicpublishconsume)
2. [RequestResponse — Wzorzec request-response](#2-requestresponse)
3. [RawMessageInterop — Interoperacyjność z systemami legacy](#3-rawmessageinterop)
4. [RabbitMQ — Kompleksowy przykład end-to-end](#4-rabbitmq)
5. [SagaOrderFlow — Cykl życia zamówienia z SAGA](#5-sagaorderflow)
6. [TransactionalOutbox — Transakcyjny outbox z deduplikacją](#6-transactionaloutbox)
7. [RetryAndDlq — Retry i Dead Letter Queue](#7-retryanddlq)
8. [BackpressureDemo — Back-pressure po stronie publish i consume](#8-backpressuredemo)
9. [ObservabilityShowcase — Pełny stos obserwowalności](#9-observabilityshowcase)
10. [MultiConsumerPartitioning — Partycjonowanie i wielu konsumentów](#10-multiconsumerpartitioning)

---

## 1. BasicPublishConsume

**Port:** `http://localhost:5100`

### Cel

Najprostszy przykład BareWire — publikowanie wiadomości przez HTTP API i konsumowanie ich z kolejki RabbitMQ z zapisem do PostgreSQL.

### Demonstrowane funkcjonalności

- **ADR-001 Raw-first** — serializacja System.Text.Json, brak envelope (koperty).
- **ADR-002 Manual topology** — exchange i kolejka deklarowane jawnie w kodzie.
- Persystencja EF Core + PostgreSQL.
- Minimal API (POST + GET).

### Architektura przepływu

```
POST /messages → MessageSent (exchange: messages.events, fanout)
    └→ MessageConsumer (queue: "messages") → zapis do PostgreSQL
```

### Workflow krok po kroku

**Krok 1: Opublikuj wiadomość**

```http
POST http://localhost:5100/messages
Content-Type: application/json

{
  "content": "Hello from BareWire!"
}
```

**Oczekiwana odpowiedź:** `202 Accepted`
```json
{
  "content": "Hello from BareWire!",
  "sentAt": "2026-03-20T12:00:00Z"
}
```

Co się dzieje w tle:
1. Endpoint tworzy rekord `MessageSent` z podaną treścią i aktualnym timestampem.
2. `IPublishEndpoint.PublishAsync()` serializuje wiadomość do surowego JSON (bez koperty) i wysyła na exchange `messages.events` (fanout).
3. RabbitMQ dostarcza wiadomość do kolejki `messages`.
4. `MessageConsumer` odbiera wiadomość, loguje ją i zapisuje jako `ReceivedMessage` do PostgreSQL.

**Krok 2: Pobierz odebrane wiadomości**

```http
GET http://localhost:5100/messages
```

**Oczekiwana odpowiedź:** `200 OK`
```json
[
  {
    "id": 1,
    "content": "Hello from BareWire!",
    "receivedAt": "2026-03-20T12:00:01Z"
  }
]
```

Wiadomość opublikowana w kroku 1 powinna być widoczna na liście (posortowanej od najnowszych). Jeśli lista jest pusta, oznacza to, że consumer jeszcze nie przetworzył wiadomości — odczekaj moment i spróbuj ponownie.

**Krok 3: Sprawdź health check**

```http
GET http://localhost:5100/health
```

**Oczekiwana odpowiedź:** `200 OK` z informacją o stanie zdrowia usługi.

### Zastosowania praktyczne

- Prosty system powiadomień (np. email, push).
- Event sourcing: publikowanie zdarzeń domenowych i zapis do read modelu.
- Integracja między mikroserwisami przez asynchroniczne zdarzenia.

---

## 2. RequestResponse

**Port:** `http://localhost:5101`

### Cel

Demonstracja wzorca request-response przez szyny wiadomości — wysłanie komendy i synchroniczne oczekiwanie na odpowiedź od konsumenta.

### Demonstrowane funkcjonalności

- **IRequestClient\<T\>** — `bus.CreateRequestClient<T>()` + `client.GetResponseAsync<TResponse>()`.
- **context.RespondAsync()** — consumer odsyła odpowiedź przez nagłówek ReplyTo.
- Persystencja historii walidacji w PostgreSQL.

### Architektura przepływu

```
POST /validate-order → ValidateOrder (exchange: order-validation, direct)
    └→ OrderValidationConsumer (queue: "order-validation")
        └→ OrderValidationResult → odpowiedź synchronicznie do wywołującego
```

### Workflow krok po kroku

**Krok 1: Wyślij żądanie walidacji zamówienia**

```http
POST http://localhost:5101/validate-order
Content-Type: application/json

{
  "orderId": "ORD-001",
  "amount": 149.99
}
```

**Oczekiwana odpowiedź:** `200 OK`
```json
{
  "orderId": "ORD-001",
  "isValid": true,
  "reason": "..."
}
```

Co się dzieje w tle:
1. Endpoint tworzy `IRequestClient<ValidateOrder>` z instancji `IBus`.
2. Wysyła komendę `ValidateOrder` na exchange `order-validation` (direct) i czeka na odpowiedź.
3. `OrderValidationConsumer` odbiera komendę, wykonuje logikę walidacji i wywołuje `context.RespondAsync(new OrderValidationResult(...))`.
4. Odpowiedź wraca do callera przez tymczasową kolejkę response (nagłówek ReplyTo).
5. Consumer zapisuje rekord walidacji do PostgreSQL.

> **Uwaga:** Jeśli consumer nie odpowie w określonym czasie, endpoint zwróci `408 Request Timeout`.

**Krok 2: Pobierz historię walidacji**

```http
GET http://localhost:5101/validations
```

**Oczekiwana odpowiedź:** `200 OK` — lista wszystkich rekordów walidacji z bazy danych, posortowana od najnowszych.

### Zastosowania praktyczne

- Walidacja danych w czasie rzeczywistym (np. sprawdzenie dostępności produktu, weryfikacja karty płatniczej).
- Synchroniczne zapytania między mikroserwisami, gdy odpowiedź jest potrzebna natychmiast.
- Wzorzec CQRS: query routed przez szynę wiadomości.

---

## 3. RawMessageInterop

**Port:** `http://localhost:5102`

### Cel

Interoperacyjność z systemami legacy — odbiór surowych wiadomości JSON publikowanych przez zewnętrzny system (bez BareWire) z niestandardowymi nagłówkami.

### Demonstrowane funkcjonalności

- **IRawConsumer** — ręczna deserializacja przez `TryDeserialize<T>()` + ekstrakcja nagłówków.
- **IConsumer\<T\>** — automatyczna deserializacja tego samego typu wiadomości na osobnej kolejce.
- **ConfigureHeaderMapping** — mapowanie nagłówków legacy (`X-Correlation-Id`, `X-Message-Type`, `X-Source-System`) na kanoniczne nagłówki BareWire.
- **LegacyPublisher** — BackgroundService symulujący system legacy (używa bezpośrednio `RabbitMQ.Client`, bez BareWire).

### Architektura przepływu

```
LegacyPublisher (RabbitMQ.Client, plain JSON) → legacy.events (fanout exchange)
    ├→ raw-events queue  → RawEventConsumer (IRawConsumer)    → PostgreSQL
    └→ typed-events queue → TypedEventConsumer (IConsumer<T>) → PostgreSQL
```

### Workflow krok po kroku

**Krok 1: Symuluj publikację z systemu legacy**

```http
POST http://localhost:5102/legacy/simulate
```

**Oczekiwana odpowiedź:** `202 Accepted`
```json
{
  "message": "Legacy event published to legacy.events exchange."
}
```

Co się dzieje w tle:
1. `LegacyPublisher` publikuje surowy JSON na exchange `legacy.events` (fanout) używając bezpośrednio `RabbitMQ.Client` (nie BareWire).
2. Wiadomość zawiera niestandardowe nagłówki: `X-Correlation-Id`, `X-Message-Type`, `X-Source-System`.
3. RabbitMQ dostarcza wiadomość do obu kolejek: `raw-events` i `typed-events`.
4. **RawEventConsumer** (IRawConsumer) odbiera surowe bajty, ręcznie deserializuje je przez `TryDeserialize<T>()` i odczytuje zmapowane nagłówki.
5. **TypedEventConsumer** (IConsumer\<ExternalEvent\>) odbiera automatycznie zdeserializowaną wiadomość.
6. Oba konsumenty zapisują przetworzony rezultat do PostgreSQL.

**Krok 2: Pobierz przetworzone wiadomości**

```http
GET http://localhost:5102/messages
```

**Oczekiwana odpowiedź:** `200 OK` — lista przetworzonych wiadomości (powinny być dwa wpisy na każdą symulację: jeden od RawEventConsumer, drugi od TypedEventConsumer).

### Zastosowania praktyczne

- Migracja z systemów legacy — stopniowe przejmowanie wiadomości ze starych systemów bez konieczności ich modyfikacji.
- Integracja z zewnętrznymi partnerami, którzy wysyłają dane w niestandardowym formacie.
- Multi-protocol gateway: odbiór wiadomości z różnych źródeł o różnych konwencjach nagłówków.

---

## 4. RabbitMQ

**Port:** `http://localhost:5076`

### Cel

Kompleksowy przykład end-to-end łączący wiele funkcjonalności BareWire: transport RabbitMQ, transactional outbox, observability, publish-side back-pressure.

### Demonstrowane funkcjonalności

- **ADR-001 Raw-first** + **ADR-002 Manual topology** + **ADR-006 Publish-side back-pressure**.
- **SAGA state machine** (`OrderSagaStateMachine`) z persystencją EF Core + SQLite.
- **Transactional outbox** — niezawodne dostarczanie wiadomości.
- **OpenTelemetry** — trace'y i metryki.
- **Health checks** — endpoint `/health`.

### Architektura przepływu

```
POST /orders → OrderCreated (exchange: rmq-sample.order.events, fanout)
    └→ OrderConsumer (queue: "orders") → przetwarza zamówienie → OrderProcessed
```

> **Uwaga:** Ten sample używa osobnego exchange `rmq-sample.order.events`, oddzielonego od exchange `order.events` używanego przez SagaOrderFlow (sekcja 5). Dzięki temu oba sample'e mogą działać równolegle bez cross-contamination wiadomości.

### Workflow krok po kroku

**Krok 1: Złóż zamówienie**

```http
POST http://localhost:5076/orders
Content-Type: application/json

{
  "amount": 250.00,
  "currency": "PLN"
}
```

**Oczekiwana odpowiedź:** `202 Accepted`
```json
{
  "orderId": "a1b2c3d4-..."
}
```

Co się dzieje w tle:
1. Generowany jest unikalny `orderId` (GUID).
2. `OrderCreated` publikowane na exchange `rmq-sample.order.events` (fanout).
3. **OrderConsumer** na kolejce `orders` odbiera zdarzenie i przetwarza zamówienie.
4. OrderConsumer wysyła `OrderProcessed` na exchange `order-processed.events`.

**Krok 2: Sprawdź status zamówienia**

```http
GET http://localhost:5076/orders/{orderId}
```

**Oczekiwana odpowiedź:** `200 OK`
```json
{
  "orderId": "a1b2c3d4-...",
  "status": "Use the saga repository to query state."
}
```

> **Uwaga:** W tej wersji endpoint jest placeholderem — w produkcji odpytywałby repozytorium sagi o aktualny stan.

### Zastosowania praktyczne

- Kompletny system przetwarzania zamówień z orkiestracją (SAGA).
- Wzorzec do budowy mikroserwisów z niezawodnym dostarczaniem wiadomości (outbox).
- Baza do dodawania kolejnych etapów pipeline'u (płatność, wysyłka, powiadomienia).

---

## 5. SagaOrderFlow

**Port:** `http://localhost:5103`

### Cel

Demonstracja pełnego cyklu życia zamówienia z maszyną stanów SAGA — od utworzenia, przez płatność i wysyłkę, po kompensację w przypadku błędu.

### Demonstrowane funkcjonalności

- **SAGA state machine** z 6 stanami i kompensacją.
- **Scheduled timeout** — timeout płatności po 30 sekundach.
- Persystencja stanu sagi w PostgreSQL (EF Core, optimistic concurrency).
- Odpytywanie stanu sagi przez REST API.

### Architektura przepływu (maszyna stanów)

```
POST /orders → OrderCreated
    └→ OrderSagaStateMachine (queue: "order-saga"):
           Initial ──OrderCreated──→ Processing  (schedule 30s timeout)
           Processing ──PaymentReceived──→ Shipping  (cancel timeout)
           Shipping ──ShipmentDispatched──→ Completed  (stan końcowy)
           Processing ──PaymentFailed──→ Compensating  (cancel timeout)
           Processing ──PaymentTimeout──→ Compensating  (po 30s bez płatności)
           Compensating ──CompensationCompleted──→ Failed  (stan końcowy)
```

### Workflow krok po kroku

**Krok 1: Utwórz zamówienie (start SAGA)**

```http
POST http://localhost:5103/orders
Content-Type: application/json

{
  "amount": 599.99,
  "shippingAddress": "ul. Marszalkowska 1, 00-001 Warszawa"
}
```

**Oczekiwana odpowiedź:** `202 Accepted`
```json
{
  "orderId": "f7e8d9c0-..."
}
```

Co się dzieje w tle:
1. Generowany jest `orderId` (GUID) i publikowany jest event `OrderCreated`.
2. `OrderSagaStateMachine` tworzy nową instancję sagi ze stanem `Processing`.
3. Uruchamiany jest timer 30s na timeout płatności (`PaymentTimeout`).

**Krok 2: Sprawdź status sagi**

```http
GET http://localhost:5103/orders/{orderId}/status
```

**Oczekiwana odpowiedź (zaraz po utworzeniu):** `200 OK`
```json
{
  "orderId": "f7e8d9c0-...",
  "currentState": "Processing",
  "amount": 599.99,
  "shippingAddress": "ul. Marszalkowska 1, 00-001 Warszawa",
  "paymentId": null,
  "trackingNumber": null,
  "failureReason": null,
  "createdAt": "2026-03-20T12:00:00Z",
  "updatedAt": "2026-03-20T12:00:00Z"
}
```

**Po upływie 30 sekund bez płatności:**
```json
{
  "currentState": "Compensating"
}
```

**Po kompensacji:**
```json
{
  "currentState": "Failed",
  "failureReason": "Payment timeout"
}
```

**Krok 3: Obserwuj pełny scenariusz sukcesu**

Aby zobaczyć pełny "happy path" (Processing → Shipping → Completed), system zewnętrzny musi opublikować zdarzenia `PaymentReceived` i `ShipmentDispatched` na exchange `order.events` w ciągu 30 sekund od utworzenia zamówienia.

### Zastosowania praktyczne

- Orkiestracja procesów biznesowych (zamówienia, rezerwacje, procesy onboardingowe).
- Long-running processes z timeoutami i kompensacją.
- Systemy wymagające audytu stanów (każda zmiana zapisana w bazie danych).
- Pattern: Saga z compensable activities (rollback w przypadku częściowego wykonania).

---

## 6. TransactionalOutbox

**Port:** `http://localhost:5104`

### Cel

Demonstracja wzorca Transactional Outbox z deduplikacją inbox — gwarancja exactly-once delivery nawet przy awarii brokera.

### Demonstrowane funkcjonalności

- **Transactional Outbox** — zapis encji biznesowej + wiadomości outbox w jednej transakcji (`SaveChangesAsync()`).
- **Inbox deduplication** — `TransactionalOutboxMiddleware` zapobiega podwójnemu przetwarzaniu przy redelivery.
- **OutboxDispatcher** — background service pollujący tabelę outbox co 1s.
- **OutboxCleanupService** — czyszczenie dostarczonych rekordów po oknie retencji.
- **Resilience** — gdy RabbitMQ jest niedostępny, wiadomości czekają w PostgreSQL.

### Architektura przepływu

```
POST /transfers
    └→ EF Core: INSERT Transfer (status="Pending") + INSERT OutboxMessage
       └→ jeden SaveChangesAsync() — atomowa transakcja

OutboxDispatcher (background, co 1s)
    └→ czyta pending OutboxMessages → publikuje TransferInitiated do RabbitMQ

TransferConsumer (queue: "transfers")
    └→ TransactionalOutboxMiddleware: sprawdzenie inbox (dedup)
    └→ UPDATE Transfer status="Completed"
```

### Workflow krok po kroku

**Krok 1: Zainicjuj przelew**

```http
POST http://localhost:5104/transfers
Content-Type: application/json

{
  "fromAccount": "PL61109010140000071219812874",
  "toAccount": "PL27114020040000300201355387",
  "amount": 1500.00
}
```

**Oczekiwana odpowiedź:** `202 Accepted`
```json
{
  "transferId": "a1b2c3d4e5f6...",
  "status": "Pending"
}
```

Co się dzieje w tle:
1. Tworzony jest rekord `Transfer` ze statusem `Pending` w tabeli aplikacyjnej.
2. `PublishAsync()` jest przechwycone przez middleware outbox — wiadomość `TransferInitiated` trafia do tabeli `OutboxMessages` (nie bezpośrednio na RabbitMQ).
3. **Oba INSERT-y** (`Transfer` + `OutboxMessage`) commitowane są w jednej transakcji PostgreSQL.
4. **OutboxDispatcher** (background service) w ciągu ~1s odczytuje pending outbox i publikuje wiadomość do RabbitMQ.
5. **TransferConsumer** odbiera `TransferInitiated`, middleware inbox sprawdza czy wiadomość nie była już przetworzona (dedup), a następnie aktualizuje `Transfer.Status` na `Completed`.

**Krok 2: Sprawdź listę przelewów**

```http
GET http://localhost:5104/transfers
```

**Oczekiwana odpowiedź:** `200 OK`
```json
[
  {
    "transferId": "a1b2c3d4e5f6...",
    "fromAccount": "PL61109010140000071219812874",
    "toAccount": "PL27114020040000300201355387",
    "amount": 1500.00,
    "status": "Completed",
    "createdAt": "2026-03-20T12:00:00Z"
  }
]
```

> **Uwaga:** Bezpośrednio po kroku 1 status może być jeszcze `Pending` — OutboxDispatcher potrzebuje do 1 sekundy na dostarczenie. Odczekaj chwilę i sprawdź ponownie.

**Krok 3: Sprawdź oczekujące wiadomości outbox**

```http
GET http://localhost:5104/outbox/pending
```

**Oczekiwana odpowiedź (po dostarczeniu):** `200 OK`
```json
{
  "pendingCount": 0,
  "note": "OutboxDispatcher polls every 1 s — pending messages are dispatched to RabbitMQ automatically."
}
```

Jeśli `pendingCount > 0`, wiadomości jeszcze czekają na dostarczenie (np. broker jest niedostępny).

### Zastosowania praktyczne

- Systemy finansowe — gwarancja, że przelew nie zostanie "zgubiony" nawet przy awarii brokera.
- Wzorzec exactly-once w systemach rozproszonych.
- Odporne na awarie pipeline'y eventów (event sourcing, CQRS).
- Migracje z systemów synchronicznych na asynchroniczne bez utraty danych.

---

## 7. RetryAndDlq

**Port:** `http://localhost:5105`

### Cel

Demonstracja polityki retry (ponownych prób) i Dead Letter Queue (kolejki martwych wiadomości) — co się dzieje z wiadomościami, których nie udało się przetworzyć.

### Demonstrowane funkcjonalności

- **Retry policy** — 3 próby z interwałem 1s między nimi.
- **Dead Letter Queue** via natywny DLX RabbitMQ (`x-dead-letter-exchange`).
- **PaymentProcessor** — symuluje bramkę płatności z 70% failure rate.
- **DlqConsumer** — odbiera wiadomości z DLQ i zapisuje jako `FailedPayment` do PostgreSQL.

### Architektura przepływu

```
POST /payments → ProcessPayment (exchange: payments, direct)
    └→ PaymentProcessor (queue: "payments", retry: 3 × 1s)
           └─ sukces: ACK (wiadomość usunięta z kolejki)
           └─ po 3 nieudanych próbach: broker dead-letteruje do "payments.dlx"
                  └→ DlqConsumer (queue: "payments-dlq") → zapis FailedPayment do PostgreSQL
```

### Workflow krok po kroku

**Krok 1: Wyślij płatność**

```http
POST http://localhost:5105/payments
Content-Type: application/json

{
  "amount": 99.99,
  "currency": "PLN"
}
```

**Oczekiwana odpowiedź:** `202 Accepted`
```json
{
  "paymentId": "d4e5f6a7-...",
  "amount": 99.99,
  "currency": "PLN"
}
```

Co się dzieje w tle:
1. Komenda `ProcessPayment` wysyłana na exchange `payments` (direct).
2. `PaymentProcessor` odbiera wiadomość — **z prawdopodobieństwem 70% rzuca `PaymentDeclinedException`**.
3. Framework automatycznie ponawia próbę do 3 razy (z 1s przerwą).
4. **Scenariusz sukcesu (~30%):** Płatność przetworzona poprawnie, wiadomość ACK.
5. **Scenariusz porażki (~70% przy każdej próbie):** Po 3 nieudanych próbach broker przenosi wiadomość na exchange `payments.dlx` → kolejkę `payments-dlq`.
6. `DlqConsumer` odbiera dead-letter i zapisuje `FailedPayment` w PostgreSQL.

**Krok 2: Sprawdź nieudane płatności (DLQ)**

```http
GET http://localhost:5105/payments/failed
```

**Oczekiwana odpowiedź:** `200 OK`
```json
[
  {
    "paymentId": "d4e5f6a7-...",
    "amount": 99.99,
    "currency": "PLN",
    "failedAt": "2026-03-20T12:00:05Z",
    "retryCount": 3
  }
]
```

> **Tip:** Wyślij kilka płatności pod rząd, aby zobaczyć mieszankę sukcesów i porażek. Część trafi do DLQ, część zostanie przetworzona poprawnie.

### Zastosowania praktyczne

- Obsługa błędów transient (np. timeout bramki płatniczej, chwilowa niedostępność API).
- Monitoring i alerting na DLQ — powiadomienie operatora o wiadomościach wymagających interwencji.
- Ręczne lub automatyczne ponowne przetwarzanie wiadomości z DLQ (replay).
- Audit trail — pełna historia nieudanych operacji.

---

## 8. BackpressureDemo

**Port:** `http://localhost:5106`

### Cel

Demonstracja mechanizmów back-pressure — co się dzieje, gdy producent publikuje szybciej niż konsument jest w stanie przetwarzać.

### Demonstrowane funkcjonalności

- **ADR-004 Credit-based flow control** (consume side): `MaxInFlightMessages = 50`, `MaxInFlightBytes = 1 MiB`.
- **ADR-006 Publish-side back-pressure**: `MaxPendingPublishes = 500` — `PublishAsync` blokuje gdy bufor jest pełny.
- **SlowConsumer** — sztuczne opóźnienie 100ms/msg (max ~80 msg/s przy 8 concurrent).
- **LoadGenerator** — konfigurowalny rate (domyślnie 1000 msg/s), start/stop przez API.
- Metryki w czasie rzeczywistym.

### Architektura przepływu

```
POST /load-test/start → LoadGenerator (BackgroundService)
    └→ publikuje LoadTestMessage z konfigurowalnę prędkością (default 1000 msg/s)
         └→ SlowConsumer (queue: "loadtest-processing", 100ms/msg)
               → ADR-006: back-pressure spowalnia publishera
               → ADR-004: flow control zatrzymuje prefetch przy MaxInFlightMessages
```

### Workflow krok po kroku

**Krok 1: Uruchom test obciążeniowy**

```http
POST http://localhost:5106/load-test/start
```

Lub z niestandardową prędkością:
```http
POST http://localhost:5106/load-test/start?rate=5000
```

**Oczekiwana odpowiedź:** `202 Accepted`
```json
{
  "status": "started",
  "targetRate": 1000,
  "startedAt": "2026-03-20T12:00:00Z"
}
```

Co się dzieje w tle:
1. `LoadGenerator` zaczyna publikować `LoadTestMessage` z zadaną prędkością.
2. `SlowConsumer` przetwarza max ~80 msg/s (100ms × 8 concurrent).
3. Szybko rośnie backlog w kolejce RabbitMQ.
4. **ADR-004:** Gdy 50 wiadomości jest in-flight, framework przestaje pobierać nowe z brokera.
5. **ADR-006:** Gdy bufor outgoing osiąga 500 pending, `PublishAsync` zaczyna blokować — LoadGenerator naturalnie zwalnia.

**Krok 2: Obserwuj metryki w czasie rzeczywistym**

```http
GET http://localhost:5106/metrics
```

**Oczekiwana odpowiedź:** `200 OK`
```json
{
  "isRunning": true,
  "totalPublished": 4523,
  "totalErrors": 0,
  "elapsedSeconds": 12.3,
  "averageRateMsgPerSec": 367.7
}
```

> **Kluczowe obserwacje:**
> - `averageRateMsgPerSec` będzie **znacznie niższy** niż `targetRate` (1000), ponieważ back-pressure spowalnia publishera.
> - Health check (`/health`) pokaże stan `Degraded` gdy bus jest pod presją (>90% `MaxInFlightMessages`).

**Krok 3: Zatrzymaj test**

```http
POST http://localhost:5106/load-test/stop
```

**Oczekiwana odpowiedź:** `200 OK`
```json
{
  "status": "stopped",
  "totalPublished": 8721,
  "totalErrors": 0
}
```

### Zastosowania praktyczne

- Testowanie odporności systemu na skoki ruchu (load testing).
- Demonstracja, że system nie traci wiadomości ani nie "wybucha" pod obciążeniem.
- Tuning parametrów flow control (`MaxInFlightMessages`, `MaxPendingPublishes`) dla konkretnego środowiska.
- Monitoring i alerting: health check degraded jako sygnał do autoskalowania.

---

## 9. ObservabilityShowcase

**Port:** `http://localhost:5107`

### Cel

Demonstracja pełnego stosu obserwowalności BareWire — rozproszone trace'y (3-hop), metryki, health checki, SAGA i outbox w jednym przykładzie.

### Demonstrowane funkcjonalności

- **OpenTelemetry** — `ActivitySource("BareWire")`, spany widoczne w Aspire Dashboard / Jaeger.
- **Metryki** — `barewire.messages.published`, `barewire.messages.consumed`, `barewire.message.duration`.
- **3-hop distributed trace** — publish → order consumer → payment consumer → shipment consumer.
- **SAGA state machine** — `DemoSagaStateMachine` (Initial → Processing → Completed).
- **Transactional outbox** — niezawodne dostarczanie z polling co 1s.
- **Health checks** — stan busa na `/health`.

### Architektura przepływu

```
POST /demo/run → DemoOrderCreated (exchange: demo.events, topic)
    ├→ DemoOrderConsumer (queue: demo-orders, routing: order.*)
    │      └→ publikuje DemoPaymentProcessed
    │            └→ DemoPaymentConsumer (queue: demo-payments, routing: payment.*)
    │                   └→ publikuje DemoShipmentDispatched
    │                         └→ DemoShipmentConsumer (queue: demo-shipments, routing: shipment.*)
    │                                └→ loguje zakończenie pipeline'u
    └→ DemoSagaStateMachine (queue: demo-saga, routing: #):
           Initial ──DemoOrderCreated──→ Processing
           Processing ──DemoPaymentProcessed──→ Completed
```

### Workflow krok po kroku

**Krok 1: Uruchom demo**

```http
POST http://localhost:5107/demo/run
```

**Oczekiwana odpowiedź:** `202 Accepted`
```json
{
  "orderId": "b2c3d4e5-...",
  "amount": 456.78,
  "message": "DemoOrderCreated published. Check Aspire Dashboard for distributed traces."
}
```

Co się dzieje w tle (3-hop chain):
1. Publikowany jest `DemoOrderCreated` na exchange `demo.events` (topic, routing key: `order.created`).
2. **Hop 1:** `DemoOrderConsumer` (queue: `demo-orders`) odbiera event i publikuje `DemoPaymentProcessed`.
3. **Hop 2:** `DemoPaymentConsumer` (queue: `demo-payments`) odbiera event i publikuje `DemoShipmentDispatched`.
4. **Hop 3:** `DemoShipmentConsumer` (queue: `demo-shipments`) odbiera event i loguje zakończenie.
5. **Równolegle:** `DemoSagaStateMachine` (queue: `demo-saga`, binding: `#`) koreluje `DemoOrderCreated` i `DemoPaymentProcessed`, przechodząc Initial → Processing → Completed.

> **Kluczowe:** Otworz Aspire Dashboard (domyślnie `http://localhost:18888`) lub inny backend OTLP (Jaeger, Zipkin). Powinieneś zobaczyć pełny rozproszoony trace przechodzący przez 3 konsumentów.

**Krok 2: Sprawdź health check**

```http
GET http://localhost:5107/health
```

**Oczekiwana odpowiedź:** `200 OK` — `Healthy` jeśli bus działa poprawnie.

### Zastosowania praktyczne

- Implementacja obserwowalności w systemach rozproszonych (distributed tracing, metrics, health).
- Debugging problemów z wydajnością (trace'y pokazują latency na każdym hopie).
- Monitoring SLA — metryki `barewire.message.duration` do dashboardów Grafana.
- Integracja z platformami observability (Jaeger, Zipkin, OTEL Collector, Aspire Dashboard).

---

## 10. MultiConsumerPartitioning

**Port:** `http://localhost:5108`

### Cel

Demonstracja wielu konsumentów na jednym endpointcie z PartitionerMiddleware — gwarancja sekwencyjnego przetwarzania per CorrelationId przy zachowaniu pełnej równoległości między różnymi CorrelationId.

### Demonstrowane funkcjonalności

- **Wielu konsumentów na jednym endpointcie** — `OrderEventConsumer`, `PaymentEventConsumer`, `ShipmentEventConsumer` zarejestrowane na jednej kolejce.
- **PartitionerMiddleware** (64 partycje) — gwarancja sekwencyjności per CorrelationId.
- **ConcurrentMessageLimit = 16** — wysoka przepustowość z zachowaniem porządku.
- Persystencja logów przetwarzania do PostgreSQL (weryfikacja porządku).

### Architektura przepływu

```
POST /events/generate (1000 eventów, 10 CorrelationId)
    └→ IBus.PublishAsync → events (topic exchange)
            └→ event-processing (queue, binding: #)
                    ├→ OrderEventConsumer    (typ: OrderEvent)
                    ├→ PaymentEventConsumer  (typ: PaymentEvent)
                    └→ ShipmentEventConsumer (typ: ShipmentEvent)
                           └→ PartitionerMiddleware (64 partycje, klucz: CorrelationId)
                                  └→ ProcessingLogEntry → PostgreSQL
```

### Workflow krok po kroku

**Krok 1: Wygeneruj burst eventów**

```http
POST http://localhost:5108/events/generate
```

**Oczekiwana odpowiedź:** `202 Accepted`
```json
{
  "published": 1000,
  "correlationIds": [
    "a1b2c3d4-...",
    "e5f6a7b8-...",
    "..."
  ]
}
```

Co się dzieje w tle:
1. Generowane jest 1000 eventów rozłożonych na 10 CorrelationId.
2. Eventy są mieszanką trzech typów: `OrderEvent`, `PaymentEvent`, `ShipmentEvent` (round-robin).
3. Wszystkie trafiają na topic exchange `events` → kolejkę `event-processing`.
4. `ConsumerDispatcher` routuje każdą wiadomość do odpowiedniego konsumenta na podstawie typu CLR.
5. **PartitionerMiddleware** gwarantuje, że wiadomości z tym samym CorrelationId są przetwarzane sekwencyjnie (w ramach jednej partycji), natomiast wiadomości z różnymi CorrelationId — w pełni równolegle.
6. Każde przetworzenie zapisywane jest jako `ProcessingLogEntry` w PostgreSQL (z timestampem i ThreadId).

**Krok 2: Zweryfikuj porządek przetwarzania**

```http
GET http://localhost:5108/events/processing-log
```

**Oczekiwana odpowiedź:** `200 OK` — lista wpisów posortowanych po `processedAt`.

**Jak zweryfikować porządek:**
- Pogrupuj wyniki po `CorrelationId`.
- Dla każdej grupy sprawdź, czy timestampy `processedAt` nie nakładają się (brak współbieżnego przetwarzania w ramach tego samego CorrelationId).
- Między grupami (różne CorrelationId) timestampy mogą się nakładać — to dowód na równoległe przetwarzanie.

### Zastosowania praktyczne

- Event processing z gwarancją porządku per entity (np. per zamówienie, per użytkownik, per konto).
- High-throughput systemy, które muszą zachować ordering w ramach partycji.
- Wzorzec partycjonowania znany z Apache Kafka, zaimplementowany na RabbitMQ.
- Systemy wymagające wielu typów eventów na jednym endpointcie (event unification).

---

## Podsumowanie

| Sample | Główna funkcjonalność | Endpointy | Port |
|--------|----------------------|-----------|------|
| BasicPublishConsume | Publish/consume + PostgreSQL | `POST/GET /messages` | 5100 |
| RequestResponse | Request-response (IRequestClient) | `POST /validate-order`, `GET /validations` | 5101 |
| RawMessageInterop | Legacy interop, IRawConsumer, header mapping | `POST /legacy/simulate`, `GET /messages` | 5102 |
| RabbitMQ | End-to-end: SAGA + outbox + observability | `POST /orders`, `GET /orders/{id}` | 5076 |
| SagaOrderFlow | SAGA state machine z kompensacją | `POST /orders`, `GET /orders/{id}/status` | 5103 |
| TransactionalOutbox | Outbox + inbox dedup (exactly-once) | `POST /transfers`, `GET /transfers`, `GET /outbox/pending` | 5104 |
| RetryAndDlq | Retry policy + Dead Letter Queue | `POST /payments`, `GET /payments/failed` | 5105 |
| BackpressureDemo | Back-pressure (publish + consume) | `POST /load-test/start\|stop`, `GET /metrics` | 5106 |
| ObservabilityShowcase | OpenTelemetry, 3-hop trace, metryki | `POST /demo/run` | 5107 |
| MultiConsumerPartitioning | Partycjonowanie, multi-consumer | `POST /events/generate`, `GET /events/processing-log` | 5108 |
