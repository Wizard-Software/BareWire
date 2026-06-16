// BareWire.Samples.CloudEventsInterop — próbka demonstrujaca interop CloudEvents 1.0.
//
// Co pokazuje ta próbka:
//   - ADR-001  Raw-first: domyślny serializer JSON bez koperty; CloudEvents jest warstwą na wierzchu.
//   - ADR-002  Manual topology: exchange i kolejki deklarowane ręcznie.
//   - ADR-007  CloudEvents dual-mode: binary (ce-* w nagłówkach) + structured (koperta JSON).
//   - RAMA „RÓŻNICA PO STRONIE ODCZYTU": jeden broadcast exchange + 3 kolejki konsumenckie.
//     Ta sama logiczna wiadomość ShipmentDispatched publikowana 3 sposobami;
//     różnica jest widoczna dopiero przy ODCZYCIE (nie przy publikacji).
//   - ServiceDefaults: OpenTelemetry observability + health checks.
//
// Architektura:
//   POST /cloudevents/publish-binary   → PublishCloudEventAsync       → ce-* headers + raw payload
//   POST /cloudevents/publish-structured → PublishCloudEventStructuredAsync → application/cloudevents+json
//   POST /barewire/publish             → PublishAsync                 → raw JSON (ADR-001)
//   Wszystkie trafiają do: exchange cloudevents-interop.events (fanout)
//       ├→ ce-binary-reader   → BinaryAwareConsumer  (czyta ce-* przez GetCloudEvent/GetCloudEventOrThrow)
//       ├→ ce-structured-reader → StructuredConsumer  (router wypakował kopertę; GetCloudEvent → null)
//       └→ ce-raw-reader      → RawConsumer          (czysty JSON; GetCloudEvent → null)
//
// Wymagania (runtime, NIE do kompilacji):
//   - Broker RabbitMQ (domyślnie: amqp://guest:guest@localhost:5672/)
//   Przy uruchomieniu przez Aspire AppHost broker jest dostarczany automatycznie.

using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire;
using BareWire.CloudEvents;
using BareWire.Transport.RabbitMQ;
using BareWire.Samples.CloudEventsInterop.Consumers;
using BareWire.Samples.CloudEventsInterop.Messages;
using BareWire.Samples.CloudEventsInterop.Services;
using BareWire.Samples.ServiceDefaults;
using BareWire.Serialization.Json;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// 1. Shared defaults: OpenTelemetry observability + health checks
// ─────────────────────────────────────────────────────────────────────────────

builder.AddServiceDefaults();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Konfiguracja
// ─────────────────────────────────────────────────────────────────────────────

string rabbitMqConnectionString =
    builder.Configuration.GetConnectionString("rabbitmq")
    ?? "amqp://guest:guest@localhost:5672/";

// ─────────────────────────────────────────────────────────────────────────────
// 3. BareWire messaging — serializer + CloudEvents + transport + topologia
// ─────────────────────────────────────────────────────────────────────────────

// Krok 1: ADR-001 — rejestruje SystemTextJsonSerializer (IMessageSerializer)
// i SystemTextJsonRawDeserializer (IDeserializerResolver). Musi być PIERWSZY.
builder.Services.AddBareWireJsonSerializer();

// Krok 2: tryb binarny CE — rejestruje CloudEventsBinaryActivation (marker singleton).
// Nagłówki ce-* są mapowane na/z BasicProperties.Headers AMQP 0-9-1 przez CloudEventBinaryHeaderMapper.
// Wymaga AddBareWireJsonSerializer() powyżej.
builder.Services.AddCloudEvents();

// Krok 3: tryb structured CE — dekoruje IDeserializerResolver routerem Content-Type.
// Wiadomości z Content-Type: application/cloudevents+json są kierowane do CloudEventsEnvelopeDeserializer.
// Wszystkie inne content-type (w tym null) pozostają przy domyślnym raw JSON (ADR-001).
// Wymaga AddBareWireJsonSerializer() powyżej.
builder.Services.AddCloudEventsEnvelope();

// Rejestracja konsumentów w DI (rozwiązywane per-wiadomość przez ConsumerDispatcher).
builder.Services.AddTransient<BinaryAwareConsumer>();
builder.Services.AddTransient<StructuredConsumer>();
builder.Services.AddTransient<RawConsumer>();

// Wątkowo-bezpieczny rejestr potwierdzeń odbioru (singleton) — zasilany przez trzy konsumenty,
// odczytywany przez GET /shipments/processed (weryfikacja E2E).
builder.Services.AddSingleton<ShipmentReceiptStore>();

// ─────────────────────────────────────────────────────────────────────────────
// 4. Topologia RabbitMQ — jeden broadcast exchange + 3 kolejki czytające
// ─────────────────────────────────────────────────────────────────────────────

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    rmq.Host(rabbitMqConnectionString);

    // Wszystkie 3 tryby publikacji trafiają do tego samego DefaultExchange.
    // Fanout z natury rozgłasza każdą wiadomość do wszystkich podpiętych kolejek.
    rmq.DefaultExchange("cloudevents-interop.events");

    // ADR-002: Manual topology — wszystkie zasoby brokera deklarowane ręcznie.
    rmq.ConfigureTopology(t =>
    {
        // Jeden broadcast (fanout) exchange — sedno re-scope (różnica po stronie odczytu).
        t.DeclareExchange("cloudevents-interop.events", ExchangeType.Fanout, durable: true);

        // 3 kolejki konsumenckie podpięte do exchange przez binding routingKey: "#".
        t.DeclareQueue("ce-binary-reader", durable: true);
        t.DeclareQueue("ce-structured-reader", durable: true);
        t.DeclareQueue("ce-raw-reader", durable: true);

        t.BindExchangeToQueue("cloudevents-interop.events", "ce-binary-reader", routingKey: "#");
        t.BindExchangeToQueue("cloudevents-interop.events", "ce-structured-reader", routingKey: "#");
        t.BindExchangeToQueue("cloudevents-interop.events", "ce-raw-reader", routingKey: "#");
    });

    // Endpoint: BinaryAwareConsumer — czyta atrybuty ce-* przez GetCloudEvent/GetCloudEventOrThrow.
    rmq.ReceiveEndpoint("ce-binary-reader", e =>
    {
        e.PrefetchCount = 16;
        e.Consumer<BinaryAwareConsumer, ShipmentDispatched>();
    });

    // Endpoint: StructuredConsumer — router Content-Type wypakował kopertę; GetCloudEvent → null.
    rmq.ReceiveEndpoint("ce-structured-reader", e =>
    {
        e.PrefetchCount = 16;
        e.Consumer<StructuredConsumer, ShipmentDispatched>();
    });

    // Endpoint: RawConsumer — czysty JSON bez metadanych CE; GetCloudEvent → null (ADR-001).
    rmq.ReceiveEndpoint("ce-raw-reader", e =>
    {
        e.PrefetchCount = 16;
        e.Consumer<RawConsumer, ShipmentDispatched>();
    });
};

builder.Services.AddBareWireRabbitMq(configureRabbitMq);
builder.Services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(configureRabbitMq);
});

// ─────────────────────────────────────────────────────────────────────────────
// 5. Budowanie aplikacji
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// ─────────────────────────────────────────────────────────────────────────────
// 6. Endpointy HTTP
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoints: /health, /health/live, /health/ready.
app.MapServiceDefaults();

// POST /cloudevents/publish-binary
// Publikuje ShipmentDispatched w trybie binarnym CloudEvents:
// nagłówki ce-* (ce-id, ce-source, ce-type, ce-specversion) + surowy payload JSON.
// BinaryAwareConsumer odczyta te nagłówki przez GetCloudEvent/GetCloudEventOrThrow.
app.MapPost("/cloudevents/publish-binary", async (
    PublishShipmentRequest req,
    IPublishEndpoint bus,
    CancellationToken ct) =>
{
    ShipmentDispatched msg = new(req.ShipmentId, req.Destination, req.Carrier);

    CloudEventContext attrs = new(
        id: Guid.NewGuid().ToString(),
        source: new Uri("/samples/cloudevents-interop/binary", UriKind.Relative),
        type: "com.barewire.sample.shipment.dispatched",
        specVersion: "1.0",
        time: DateTimeOffset.UtcNow);

    await bus.PublishCloudEventAsync(msg, attrs, ct);

    return Results.Accepted($"/shipments/{req.ShipmentId}", new { req.ShipmentId, mode = "binary" });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("PublishBinaryCloudEvent");

// POST /cloudevents/publish-structured
// Publikuje ShipmentDispatched w trybie structured CloudEvents:
// koperta application/cloudevents+json zawierająca pola CE (id, source, type) i "data".
// StructuredConsumer otrzymuje gotowy obiekt ShipmentDispatched (koperta wypakowana przez router).
app.MapPost("/cloudevents/publish-structured", async (
    PublishShipmentRequest req,
    IPublishEndpoint bus,
    CancellationToken ct) =>
{
    ShipmentDispatched msg = new(req.ShipmentId, req.Destination, req.Carrier);

    CloudEventContext attrs = new(
        id: Guid.NewGuid().ToString(),
        source: new Uri("/samples/cloudevents-interop/structured", UriKind.Relative),
        type: "com.barewire.sample.shipment.dispatched",
        specVersion: "1.0",
        time: DateTimeOffset.UtcNow);

    await bus.PublishCloudEventStructuredAsync(msg, attrs, ct);

    return Results.Accepted($"/shipments/{req.ShipmentId}", new { req.ShipmentId, mode = "structured" });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("PublishStructuredCloudEvent");

// POST /barewire/publish
// Publikuje ShipmentDispatched w trybie raw JSON (ADR-001):
// brak nagłówków ce-* i brak koperty CloudEvents.
// RawConsumer potwierdza GetCloudEvent() → null (brak metadanych CE).
app.MapPost("/barewire/publish", async (
    PublishShipmentRequest req,
    IPublishEndpoint bus,
    CancellationToken ct) =>
{
    ShipmentDispatched msg = new(req.ShipmentId, req.Destination, req.Carrier);

    await bus.PublishAsync(msg, ct);

    return Results.Accepted($"/shipments/{req.ShipmentId}", new { req.ShipmentId, mode = "raw" });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("PublishRaw");

// GET /shipments/processed
// Zwraca wszystkie potwierdzenia odbioru zarejestrowane przez trzy konsumenty
// (BinaryAware / Structured / Raw). Używane przez testy E2E do weryfikacji, że fanout
// rozgłosił wiadomość i że metadane CE są widoczne zgodnie z trybem publikacji.
app.MapGet("/shipments/processed", (ShipmentReceiptStore receipts) =>
    Results.Ok(receipts.GetAll()))
    .Produces<IReadOnlyList<ShipmentReceipt>>(StatusCodes.Status200OK)
    .WithName("GetProcessedShipments");

app.Run();
