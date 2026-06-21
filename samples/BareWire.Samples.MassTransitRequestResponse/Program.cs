// BareWire.Samples.MassTransitRequestResponse — demonstruje scenariusz GH #19 (zadanie B2):
// BareWire wysyła request przez IRequestClient<T> do PRAWDZIWEGO busa MassTransit,
// który odpowiada przez RespondAsync, a BareWire odbiera odpowiedź Response<TResponse>.
//
// Co demonstruje ten sample:
//   - ADR-021  BareWire→MassTransit request/response: klient BareWire ustawia pole
//              responseAddress w kopercie na rabbitmq://host/amq.rabbitmq.reply-to.
//              MassTransit wykrywa ten sufiks przez IsReplyToAddress() i kieruje odpowiedź
//              przez AMQP ReplyTo (pole na wiadomości AMQP = nazwa wyłącznej kolejki
//              odpowiedzi BareWire), zamiast próbować wysłać na zwykły exchange.
//   - ADR-001  Raw-first: domyślny serializer pozostaje raw JSON. MassTransitEnvelopeSerializer
//              jest aktywowany tylko dla CheckOrderStatus przez MapSerializer<T,S>().
//   - ADR-002  Manual topology: ConfigureConsumeTopology = false po stronie MT — BareWire
//              dociera do kolejki przez domyślny exchange AMQP (routing key = nazwa kolejki).
//   - Kolejność DI: AddBareWireJsonSerializer() PRZED AddMassTransitEnvelopeSerializer()
//              i AddMassTransitEnvelopeDeserializer() — odwrotna kolejność rzuca
//              InvalidOperationException przy starcie.
//
// Architektura:
//
//   POST /order-status
//     → IRequestClient<CheckOrderStatus> (BareWire)
//         → publikuje na "" (default AMQP exchange), routing key = "mt-order-status"
//           Content-Type: application/vnd.masstransit+json
//           responseAddress: rabbitmq://host/amq.rabbitmq.reply-to
//         → kolejka "mt-order-status" → OrderStatusResponder (MassTransit IConsumer<T>)
//             → context.RespondAsync(new OrderStatus(...))
//               → MT wykrywa amq.rabbitmq.reply-to → routuje przez AMQP ReplyTo
//         → BareWire odbiera na swojej tymczasowej kolejce odpowiedzi
//         → Response<OrderStatus> zwracany do klienta HTTP
//
// Wymagania (runtime, NIE wymagane do kompilacji):
//   - Broker RabbitMQ (domyślnie: amqp://guest:guest@localhost:5672/)
//   Przy uruchomieniu przez Aspire AppHost broker jest udostępniany automatycznie.

// BareWire and MassTransit share several type names (IBus, IRequestClient<T>, Response<T>).
// Using aliases disambiguate at the call sites in this file.
using BwIBus = BareWire.Abstractions.IBus;
using BwIRequestClient = BareWire.Abstractions.IRequestClient<BareWire.Samples.MassTransitRequestResponse.Messages.CheckOrderStatus>;
using BwResponse = BareWire.Abstractions.Response<BareWire.Samples.MassTransitRequestResponse.Messages.OrderStatus>;

using BareWire.Abstractions.Configuration;
using BareWire;
using BareWire.Transport.RabbitMQ;
using BareWire.Serialization.Json;
using BareWire.Interop.MassTransit;
using BareWire.Samples.MassTransitRequestResponse.Consumers;
using BareWire.Samples.MassTransitRequestResponse.Messages;
using BareWire.Samples.ServiceDefaults;
using MassTransit;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// 1. Shared defaults: OpenTelemetry observability + health checks
// ─────────────────────────────────────────────────────────────────────────────

builder.AddServiceDefaults();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Configuration
// ─────────────────────────────────────────────────────────────────────────────

string rabbitMqConnectionString =
    builder.Configuration.GetConnectionString("rabbitmq")
    ?? "amqp://guest:guest@localhost:5672/";

// MassTransit wymaga formatu amqp://user:pass@host:port/vhost.
// Aspire wstrzykuje amqp:// URL, który MT akceptuje bezpośrednio.
Uri rabbitMqUri = new(rabbitMqConnectionString);
string mtRabbitUri = $"amqp://{rabbitMqUri.UserInfo}@{rabbitMqUri.Host}:{rabbitMqUri.Port}{rabbitMqUri.AbsolutePath}";

// Nazwa kolejki, na którą BareWire wysyła request, a MT nasłuchuje.
// BareWire: targetExchange = "" (default AMQP exchange), routingKey = queueName.
// MassTransit: ReceiveEndpoint(queueName, ep => ep.ConfigureConsumeTopology = false).
// ConfigureConsumeTopology = false zapobiega tworzeniu fanout exchange przez MT —
// BareWire trafia do kolejki bezpośrednio przez routing key w default AMQP exchange.
const string RequestQueueName = "mt-order-status";

// ─────────────────────────────────────────────────────────────────────────────
// 3. BareWire serialization — ADR-001 raw-first + MassTransit envelope interop
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — rejestruje SystemTextJsonSerializer i SystemTextJsonRawDeserializer.
// WAŻNE: musi być wywołane PRZED AddMassTransitEnvelopeSerializer/Deserializer.
builder.Services.AddBareWireJsonSerializer();

// Rejestruje MassTransitEnvelopeDeserializer dla Content-Type application/vnd.masstransit+json.
// BareWire używa go do zdekodowania odpowiedzi MT (IDeserializerResolver wybiera deserializer
// na podstawie Content-Type w nagłówku odpowiedzi).
builder.Services.AddMassTransitEnvelopeDeserializer();

// Rejestruje MassTransitEnvelopeSerializer w DI do użycia per-message-type.
// Aktywowany dla CheckOrderStatus przez MapSerializer<T,S>() poniżej.
builder.Services.AddMassTransitEnvelopeSerializer();

// ─────────────────────────────────────────────────────────────────────────────
// 4. BareWire transport + topology
// ─────────────────────────────────────────────────────────────────────────────

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    rmq.Host(rabbitMqConnectionString);

    // ADR-002: Manual topology — BareWire nie tworzy żadnych zasobów dla kolejki MT.
    // MT sam deklaruje kolejkę "mt-order-status" przez ReceiveEndpoint poniżej.
    // BareWire wysyła na domyślny exchange AMQP ("") z routing key = RequestQueueName,
    // co odpowiada bezpośredniemu skierowaniu do kolejki o tej nazwie.
    rmq.ConfigureTopology(_ => { });
};

builder.Services.AddBareWireRabbitMq(configureRabbitMq);
builder.Services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(configureRabbitMq);

    // Kluczowy element: MapSerializer<CheckOrderStatus, MassTransitEnvelopeSerializer>()
    // nakazuje BareWire pakować każdy CheckOrderStatus w kopertę MassTransit.
    // Koperta zawiera pola messageId, requestId, responseAddress (= amq.rabbitmq.reply-to),
    // correlationId, messageType i payload — dokładnie to, czego oczekuje MassTransit.
    cfg.MapSerializer<CheckOrderStatus, MassTransitEnvelopeSerializer>();
});

// ─────────────────────────────────────────────────────────────────────────────
// 5. MassTransit bus — prawdziwy responder (strona MT w scenariuszu interop)
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderStatusResponder>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(new Uri(mtRabbitUri));

        // ReceiveEndpoint z nazwą kolejki = RequestQueueName — MT nasłuchuje na tej kolejce.
        // ConfigureConsumeTopology = false: MT NIE tworzy fanout exchange dla CheckOrderStatus.
        // Dzięki temu BareWire może dotrzeć do kolejki przez domyślny exchange AMQP
        // (routing key = RequestQueueName), bez potrzeby bindowania exchange-to-queue.
        cfg.ReceiveEndpoint(RequestQueueName, ep =>
        {
            ep.ConfigureConsumeTopology = false;
            ep.Consumer<OrderStatusResponder>(ctx);
        });
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// 6. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// ─────────────────────────────────────────────────────────────────────────────
// 7. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

app.MapServiceDefaults();

// POST /order-status — wysyła CheckOrderStatus przez BareWire IRequestClient<T>
// do MassTransit OrderStatusResponder i zwraca odpowiedź Response<OrderStatus>.
//
// Przepływ wiadomości:
//   1. BareWire serializuje CheckOrderStatus jako kopertę MT (MapSerializer<T,S>).
//   2. Koperta trafia na domyślny exchange AMQP ("") z routing key "mt-order-status".
//   3. MT odbiera z kolejki "mt-order-status", wywołuje OrderStatusResponder.Consume().
//   4. RespondAsync() kieruje odpowiedź przez AMQP ReplyTo (amq.rabbitmq.reply-to).
//   5. BareWire odbiera OrderStatus, dekoduje przez MassTransitEnvelopeDeserializer.
//   6. IRequestClient<CheckOrderStatus>.GetResponseAsync<OrderStatus>() zwraca wynik.
app.MapPost("/order-status", async (
    OrderStatusRequest request,
    BwIBus bus,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    Log.SendingRequest(logger, request.OrderId);

    BwIRequestClient client =
        await bus.CreateRequestClientAsync<CheckOrderStatus>(cancellationToken);

    BwResponse response = await client
        .GetResponseAsync<OrderStatus>(
            new CheckOrderStatus(request.OrderId),
            cancellationToken);

    Log.ReceivedResponse(logger, response.Message.OrderId, response.Message.Status, response.Message.ProcessedBy);

    return Results.Ok(response.Message);
})
.Produces<OrderStatus>()
.ProducesProblem(StatusCodes.Status408RequestTimeout)
.WithName("CheckOrderStatus")
.WithSummary("Sprawdź status zamówienia przez BareWire→MassTransit request/response.");

app.Run();

// ─────────────────────────────────────────────────────────────────────────────
// HTTP request model
// ─────────────────────────────────────────────────────────────────────────────

internal sealed record OrderStatusRequest(string OrderId);

// ─────────────────────────────────────────────────────────────────────────────
// High-performance logging (CA1848 / CA1873)
// ─────────────────────────────────────────────────────────────────────────────

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "BareWire: wysyłam zapytanie o status zamówienia {OrderId} przez IRequestClient<CheckOrderStatus>")]
    internal static partial void SendingRequest(ILogger logger, string orderId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "BareWire: otrzymano odpowiedź — OrderId={OrderId}, Status={Status}, ProcessedBy={ProcessedBy}")]
    internal static partial void ReceivedResponse(ILogger logger, string orderId, string status, string processedBy);
}
