# BareWire.Samples.MassTransitInterop

Sample demonstrujący koegzystencję BareWire z systemem MassTransit na jednym brokerze RabbitMQ. Oba systemy działają równolegle — każdy ze swoim formatem serializacji — bez żadnych modyfikacji po stronie consumerów.

## Scenariusz

Trzy niezależne potoki wiadomości na tym samym brokerze RabbitMQ:

- **Potok MassTransit (odbiór)** — symulowany producent (`MassTransitSimulator`) publikuje wiadomości w formacie koperty MassTransit (`application/vnd.masstransit+json`). BareWire odbiera je przez `MassTransitEnvelopeDeserializer`, który rozpakuje kopertę i dostarczy czysty obiekt `OrderCreated` do consumera.
- **Potok BareWire** — `IBus.PublishAsync<OrderCreated>()` publikuje raw JSON (`application/json`) zgodnie z ADR-001. Consumer odbiera go bezpośrednio przez `SystemTextJsonRawDeserializer`.
- **Potok MT envelope publish** — `IBus.PublishAsync<OrderCreated>()` z per-endpoint override `UseSerializer<MassTransitEnvelopeSerializer>()` publikuje wiadomość w formacie koperty MassTransit. Domyślny serializer raw JSON (ADR-001) pozostaje niezmieniony dla pozostałych endpointów.

Wszystkie consumery implementują ten sam interfejs `IConsumer<OrderCreated>` i operują na tym samym typie wiadomości — niezależnie od formatu źródłowego.

## Co demonstruje ten sample

| Aspekt | Szczegół |
|--------|----------|
| ADR-001 raw-first | Endpoint `/barewire/publish` publikuje czysty JSON bez koperty |
| ADR-002 manual topology | Jawna deklaracja exchange, queue i bindingu — brak auto-topology |
| ADR-005 MT naming | `IConsumer<T>`, `ConsumeContext<T>`, `IBus` — konwencje znane z MassTransit |
| `ContentTypeDeserializerRouter` | Automatyczny wybór deserializatora na podstawie nagłówka `Content-Type` |
| `MassTransitEnvelopeDeserializer` | Rozpakowanie koperty MT i ekstrakcja payloadu do docelowego typu |
| Per-endpoint serializer override | `UseSerializer<MassTransitEnvelopeSerializer>()` — publikacja w formacie MT dla konkretnego endpointu, bez wpływu na domyślny serializer |

## Architektura

```
MassTransitSimulator (RabbitMQ.Client) → mt-orders (direct exchange)
    └→ mt-orders-queue → MassTransitOrderConsumer (IConsumer<OrderCreated>)
         Content-Type: application/vnd.masstransit+json
         → ContentTypeDeserializerRouter → MassTransitEnvelopeDeserializer → OrderCreated

BareWire IBus.PublishAsync() → barewire-orders (fanout exchange)
    └→ barewire-orders-queue → BareWireOrderConsumer (IConsumer<OrderCreated>)
         Content-Type: application/json
         → ContentTypeDeserializerRouter → SystemTextJsonRawDeserializer → OrderCreated

BareWire IBus.PublishAsync() [UseSerializer<MassTransitEnvelopeSerializer>()]
    → mt-envelope-publish (direct exchange)
    └→ mt-envelope-publish-queue → MassTransitOrderConsumer (IConsumer<OrderCreated>)
         Content-Type: application/vnd.masstransit+json (per-endpoint override)
         → ContentTypeDeserializerRouter → MassTransitEnvelopeDeserializer → OrderCreated
```

## Endpointy HTTP

| Metoda | Ścieżka | Opis |
|--------|---------|------|
| `POST` | `/masstransit/simulate` | Publikuje jedną wiadomość w formacie koperty MassTransit na exchange `mt-orders` |
| `POST` | `/barewire/publish` | Publikuje raw JSON przez `IBus.PublishAsync<OrderCreated>()` |
| `POST` | `/masstransit/publish-envelope` | Publikuje wiadomość w formacie koperty MassTransit przez `IBus.PublishAsync` z per-endpoint `UseSerializer<MassTransitEnvelopeSerializer>()` |

Wszystkie endpointy zwracają `202 Accepted`. Payloady są generowane wewnętrznie — endpointy nie przyjmują danych od klienta.

Oprócz endpointów `MassTransitSimulator` działa jako `BackgroundService` i publikuje wiadomość automatycznie co 5 sekund.

## Jak uruchomić

### Przez Aspire AppHost (zalecane)

```bash
dotnet run --project samples/BareWire.Samples.AppHost/
```

Aspire uruchomi RabbitMQ w kontenerze Docker oraz wszystkie projekty sample w odpowiedniej kolejności. Dashboard Aspire dostępny jest pod adresem wskazanym w konsoli.

### Standalone

Wymagania: działający broker RabbitMQ (domyślnie `amqp://guest:guest@localhost:5672/`).

```bash
dotnet run --project samples/BareWire.Samples.MassTransitInterop/
```

## Konfiguracja

| Connection string | Domyślna wartość | Opis |
|-------------------|-----------------|------|
| `rabbitmq` | `amqp://guest:guest@localhost:5672/` | Adres brokera RabbitMQ |

W trybie Aspire connection string jest wstrzykiwany automatycznie. W trybie standalone można go nadpisać przez `appsettings.json` lub zmienną środowiskową:

```json
{
  "ConnectionStrings": {
    "rabbitmq": "amqp://user:password@rabbitmq-host:5672/"
  }
}
```

## Ważna uwaga: kolejność rejestracji DI

`AddMassTransitEnvelopeDeserializer()` i `AddMassTransitEnvelopeSerializer()` MUSZĄ być wywołane PO `AddBareWireJsonSerializer()`. Rejestracja w odwrotnej kolejności spowoduje `InvalidOperationException` przy starcie aplikacji.

```csharp
// Prawidłowa kolejność:
services.AddBareWireJsonSerializer();           // musi być pierwszy
services.AddMassTransitEnvelopeDeserializer();  // musi być po JsonSerializer
services.AddMassTransitEnvelopeSerializer();    // musi być po JsonSerializer
```
