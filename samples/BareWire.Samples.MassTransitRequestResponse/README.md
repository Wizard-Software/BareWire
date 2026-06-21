# BareWire.Samples.MassTransitRequestResponse

Sample demonstrujący scenariusz [GH #19](https://github.com/Wizard-Software/BareWire/issues/19): klient BareWire wysyła request przez `IRequestClient<CheckOrderStatus>` do **prawdziwego busa MassTransit**, który odpowiada przez `RespondAsync`, a BareWire odbiera `Response<OrderStatus>` z powrotem.

## Scenariusz

Dwie strony działają w tym samym procesie, na tym samym brokerze RabbitMQ:

- **BareWire (klient)** — wysyła `CheckOrderStatus` przez `IRequestClient<T>` z serializerem `MassTransitEnvelopeSerializer` (aktywowanym przez `MapSerializer<CheckOrderStatus, MassTransitEnvelopeSerializer>()`). Rejestruje tymczasową kolejkę odpowiedzi i oczekuje na `Response<OrderStatus>`.
- **MassTransit (responder)** — `OrderStatusResponder` (implementuje `IConsumer<CheckOrderStatus>`) odbiera request z kolejki `mt-order-status` i wywołuje `context.RespondAsync(new OrderStatus(...))`.

## Co demonstruje ten sample

| Aspekt | Szczegół |
|--------|----------|
| BareWire→MT request/response | BareWire ustawia `responseAddress` na `amq.rabbitmq.reply-to`; MT kieruje odpowiedź przez AMQP `ReplyTo` |
| Raw-first | Domyślny serializer BareWire pozostaje raw JSON; `MassTransitEnvelopeSerializer` aktywowany tylko dla `CheckOrderStatus` |
| Manual topology | `ConfigureConsumeTopology = false` po stronie MT; BareWire dociera do kolejki przez domyślny exchange AMQP |
| `MapSerializer<T, S>()` | Per-message-type override serializera — nie zmienia domyślnego serializera dla innych typów |
| `MapRoutingKey<T>()` | Kieruje request do kolejki respondera MT (domyślny exchange AMQP, routing key = nazwa kolejki) |
| Kolejność DI | `AddBareWireJsonSerializer()` PRZED `AddMassTransitEnvelopeSerializer/Deserializer()` |
| `IRequestClient<T>.GetResponseAsync<TResponse>()` | Typowany odbiór odpowiedzi z dopasowaniem po AMQP `CorrelationId` (fallback po `requestId` z koperty) |

## Architektura

```
POST /order-status
  → IRequestClient<CheckOrderStatus> (BareWire)
      → "" (default AMQP exchange), routing key = "mt-order-status"
        Content-Type: application/vnd.masstransit+json
        responseAddress: rabbitmq://host/amq.rabbitmq.reply-to  ← kluczowe pole
      → kolejka "mt-order-status" → OrderStatusResponder (MassTransit IConsumer<T>)
          → context.RespondAsync(new OrderStatus(...))
            → MT: IsReplyToAddress("amq.rabbitmq.reply-to") == true
              → routuje odpowiedź przez AMQP ReplyTo (tymczasowa kolejka BareWire)
      → BareWire: MassTransitEnvelopeDeserializer dekoduje odpowiedź
  → Response<OrderStatus> zwrócona do klienta HTTP
```

### Kluczowy mechanizm routowania odpowiedzi

BareWire (przez `RabbitMqEndpointAddress.BuildReplyToAddress`) ustawia pole `responseAddress` w kopercie MassTransit na:

```
rabbitmq://host[:port]/[vhost/]amq.rabbitmq.reply-to
```

MassTransit sprawdza ten adres metodą `IsReplyToAddress()` — jeśli sufiks to `amq.rabbitmq.reply-to`, MT wysyła odpowiedź przez domyślny exchange AMQP z kluczem routowania równym wartości pola `ReplyTo` na wiadomości AMQP. Pole `ReplyTo` zawiera prawdziwą nazwę wyłącznej kolejki odpowiedzi BareWire (przydzieloną przez broker), a nie literał `amq.rabbitmq.reply-to`.

Gdyby zamiast tego `responseAddress` wskazywał serwerowo-nazwaną kolejkę odpowiedzi (`rabbitmq://host/amq.gen-...`), MassTransit potraktowałby ostatni segment ścieżki jako exchange typu fanout, do którego wyłączna kolejka odpowiedzi nie jest podpięta — i odpowiedź zostałaby zgubiona (objaw z GH #19).

## Jak uruchomić

### Przez Aspire AppHost (zalecane)

```bash
dotnet run --project samples/BareWire.Samples.AppHost/
```

Aspire uruchomi RabbitMQ w kontenerze Docker oraz wszystkie projekty sample w odpowiedniej kolejności. Dashboard Aspire dostępny jest pod adresem wskazanym w konsoli.

### Standalone

Wymagania: działający broker RabbitMQ (domyślnie `amqp://guest:guest@localhost:5672/`).

```bash
dotnet run --project samples/BareWire.Samples.MassTransitRequestResponse/
```

### Testowanie endpointu

```bash
curl -X POST http://localhost:5111/order-status \
     -H "Content-Type: application/json" \
     -d '{"orderId": "ORD-12345"}'
```

Oczekiwana odpowiedź:

```json
{
  "orderId": "ORD-12345",
  "status": "Confirmed",
  "processedBy": "MassTransit/OrderStatusResponder"
}
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
