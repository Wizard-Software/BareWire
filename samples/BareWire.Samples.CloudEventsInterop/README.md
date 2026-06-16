# BareWire.Samples.CloudEventsInterop

Próbka demonstrująca interop CloudEvents 1.0 z biblioteką BareWire. Pokazuje, jak ta sama logiczna
wiadomość (`ShipmentDispatched`) może być publikowana trzema sposobami i jak każdy konsument
widzi ją inaczej — **różnica jest widoczna po stronie ODCZYTU, nie publikacji**.

## Rama: „różnica po stronie ODCZYTU"

Próbka celowo **nie** izoluje trybów publikacji per-kolejka. Dlaczego to niemożliwe?

1. **Fanout z natury rozgłasza**: exchange `cloudevents-interop.events` jest typu `fanout` —
   każda opublikowana wiadomość (niezależnie od trybu) trafia do **wszystkich** podpiętych kolejek.
2. **Brak publicznego override exchange**: `PublishAsync`, `PublishCloudEventAsync`
   i `PublishCloudEventStructuredAsync` wszystkie kierują do `DefaultExchange` —
   brak publicznego API do nadpisania exchange per-wywołanie.

Dlatego architektura wygląda tak:

```
POST /cloudevents/publish-binary     → PublishCloudEventAsync       ─┐
POST /cloudevents/publish-structured → PublishCloudEventStructuredAsync ─┤→ exchange: cloudevents-interop.events (fanout)
POST /barewire/publish               → PublishAsync (raw JSON)      ─┘
                                                                         │
                                            ┌────────────────────────────┤
                                            ↓                            ↓                           ↓
                                     ce-binary-reader          ce-structured-reader          ce-raw-reader
                                   BinaryAwareConsumer          StructuredConsumer            RawConsumer
```

Każda z trzech publikacji trafia do **wszystkich trzech kolejek**. Dopiero przy odczycie widać różnicę
w dostępnych metadanych.

## Tabela: tryb publikacji → API → co widzi konsument

| Tryb publikacji | API BareWire | Co widzi konsument przy odczycie |
|-----------------|-------------|----------------------------------|
| **binary** | `PublishCloudEventAsync(msg, attrs, ct)` | `GetCloudEvent()` zwraca `ICloudEventAttributes` z `id`, `source`, `type` (z nagłówków `ce-*`). `GetCloudEventOrThrow()` waliduje CE 1.0. |
| **structured** | `PublishCloudEventStructuredAsync(msg, attrs, ct)` | Koperta `application/cloudevents+json` wypakowana przez router Content-Type (`AddCloudEventsEnvelope()`). `context.Message` = gotowy `ShipmentDispatched`. `GetCloudEvent()` → `null` (atrybuty CE są w kopercie, nie w `ce-*`). |
| **raw** | `PublishAsync(msg, ct)` | Czysty JSON (ADR-001). Brak nagłówków `ce-*`, brak koperty. `GetCloudEvent()` → `null`. |

## Kolejność rejestracji DI (krytyczna)

```csharp
builder.Services.AddBareWireJsonSerializer();   // 1. rejestruje IMessageSerializer + IDeserializerResolver
builder.Services.AddCloudEvents();              // 2. tryb binarny (marker CloudEventsBinaryActivation)
builder.Services.AddCloudEventsEnvelope();      // 3. tryb structured (dekoruje IDeserializerResolver routerem Content-Type)
```

Kolejność jest obowiązkowa: `AddCloudEvents()` i `AddCloudEventsEnvelope()` wymagają,
aby `AddBareWireJsonSerializer()` był wywołany jako pierwszy.

## Endpointy HTTP

| Metoda | Ścieżka | Opis |
|--------|---------|------|
| `POST` | `/cloudevents/publish-binary` | Publikuje w trybie binarnym CE (nagłówki `ce-*`) |
| `POST` | `/cloudevents/publish-structured` | Publikuje w trybie structured CE (koperta JSON) |
| `POST` | `/barewire/publish` | Publikuje raw JSON bez metadanych CE (ADR-001) |
| `GET` | `/health` | Health check (BareWire bus + system) |

Plik `.http` w katalogu próbki zawiera gotowe żądania do testowania.

## Jak uruchomić

### Przez Aspire AppHost (zalecane)

```bash
dotnet run --project samples/BareWire.Samples.AppHost/BareWire.Samples.AppHost.csproj
```

Aspire automatycznie uruchamia kontener RabbitMQ i wstrzykuje connection string przez
`.WithReference(rabbitmq)`. Próbka dostępna pod adresem wyświetlanym w dashboardzie Aspire.

### Bezpośrednio (wymaga lokalnego RabbitMQ)

```bash
# Uruchom RabbitMQ lokalnie (np. przez Docker):
docker run -d -p 5672:5672 -p 15672:15672 rabbitmq:management

# Uruchom próbkę:
dotnet run --project samples/BareWire.Samples.CloudEventsInterop/BareWire.Samples.CloudEventsInterop.csproj
```

Następnie użyj pliku `.http` lub `curl`:

```bash
curl -X POST http://localhost:5077/cloudevents/publish-binary \
  -H "Content-Type: application/json" \
  -d '{"shipmentId":"SHIP-001","destination":"Warszawa","carrier":"DHL"}'
```

Obserwuj logi konsumentów — każdy z nich loguje co innego dla tej samej wiadomości.

---

## Nota R1 — CloudEvents-over-RabbitMQ (AMQP 0-9-1)

Mapowanie atrybutów `ce-*` na nagłówki transportowe w tej próbce korzysta z **AMQP 0-9-1
`BasicProperties.Headers`** — to jest wzorzec „CloudEvents-over-RabbitMQ". NIE jest to
certyfikowany **AMQP 1.0 binding** zdefiniowany przez specyfikację CloudEvents
(który wymaga protokołu AMQP 1.0, np. przez Azure Service Bus lub Apache ActiveMQ Artemis).

RabbitMQ domyślnie używa AMQP 0-9-1. Jeśli potrzebujesz certyfikowanego bindingu AMQP 1.0,
konieczny jest broker zgodny z AMQP 1.0 (patrz ADR-007 R1).

## Nota SEC-2 — dane wrażliwe a atrybuty `ce-*`

Atrybuty CloudEvents (`ce-id`, `ce-source`, `ce-type` itp.) są widoczne dla brokera RabbitMQ
oraz dla dowolnego middleware logującego nagłówki wiadomości. **Dane wrażliwe** (np. dane osobowe,
numery kart kredytowych, sekrety) **należą do payloadu `data`**, nie do atrybutów `ce-*`.

W tej próbce:
- `ce-source`, `ce-type` — zawierają tylko niesensytywne identyfikatory próbki.
- `ShipmentDispatched.Destination`, `Carrier` — dane biznesowe w polu `data` (payload JSON).

W środowiskach produkcyjnych rozważ szyfrowanie payloadu `data` jeśli zawiera dane wrażliwe
(patrz `security-architecture.md`, sekcja payload encryption).
