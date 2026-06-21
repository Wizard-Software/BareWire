// BareWire.Samples.MassTransitRequestResponse — demonstrates the BareWire->MassTransit
// request/response scenario: BareWire sends a request via IRequestClient<T> to a REAL
// MassTransit bus, which replies via RespondAsync, and BareWire receives the Response<TResponse>.
//
// What this sample shows:
//   - BareWire->MassTransit request/response: the BareWire client sets the envelope's
//     responseAddress to rabbitmq://host/amq.rabbitmq.reply-to. MassTransit detects this
//     suffix via IsReplyToAddress() and routes the reply through the AMQP ReplyTo header
//     (set to BareWire's exclusive reply-queue name), instead of publishing to an exchange.
//   - Raw-first: the default serializer stays raw JSON. MassTransitEnvelopeSerializer is
//     activated only for CheckOrderStatus via MapSerializer<T,S>().
//   - Manual topology: ConfigureConsumeTopology = false on the MassTransit side — BareWire
//     reaches the queue through the default AMQP exchange (routing key = queue name).
//   - DI ordering: AddBareWireJsonSerializer() BEFORE AddMassTransitEnvelopeSerializer() and
//     AddMassTransitEnvelopeDeserializer() — the reverse order throws at startup.
//
// Architecture:
//
//   POST /order-status
//     -> IRequestClient<CheckOrderStatus> (BareWire)
//         -> publishes to "" (default AMQP exchange), routing key = "mt-order-status"
//            Content-Type: application/vnd.masstransit+json
//            responseAddress: rabbitmq://host/amq.rabbitmq.reply-to
//         -> queue "mt-order-status" -> OrderStatusResponder (MassTransit IConsumer<T>)
//             -> context.RespondAsync(new OrderStatus(...))
//               -> MassTransit detects amq.rabbitmq.reply-to -> routes via the AMQP ReplyTo header
//         -> BareWire receives on its temporary reply queue
//         -> Response<OrderStatus> returned to the HTTP caller
//
// Runtime requirements (NOT required to compile):
//   - A RabbitMQ broker (default: amqp://guest:guest@localhost:5672/).
//   When run via the Aspire AppHost the broker is provisioned automatically.

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

// MassTransit expects an amqp://user:pass@host:port/vhost URI.
// Aspire injects an amqp:// URL that MassTransit accepts directly.
Uri rabbitMqUri = new(rabbitMqConnectionString);
string mtRabbitUri = $"amqp://{rabbitMqUri.UserInfo}@{rabbitMqUri.Host}:{rabbitMqUri.Port}{rabbitMqUri.AbsolutePath}";

// The queue BareWire sends the request to, and MassTransit listens on.
// BareWire: targetExchange = "" (default AMQP exchange), routingKey = queueName.
// MassTransit: ReceiveEndpoint(queueName, ep => ep.ConfigureConsumeTopology = false).
// ConfigureConsumeTopology = false stops MassTransit from creating a fanout exchange —
// BareWire reaches the queue directly via the routing key on the default AMQP exchange.
const string RequestQueueName = "mt-order-status";

// ─────────────────────────────────────────────────────────────────────────────
// 3. BareWire serialization — raw-first + MassTransit envelope interop
// ─────────────────────────────────────────────────────────────────────────────

// Raw-first: registers SystemTextJsonSerializer and SystemTextJsonRawDeserializer.
// IMPORTANT: must be called BEFORE AddMassTransitEnvelopeSerializer/Deserializer.
builder.Services.AddBareWireJsonSerializer();

// Registers MassTransitEnvelopeDeserializer for Content-Type application/vnd.masstransit+json.
// BareWire uses it to decode the MassTransit reply (the IDeserializerResolver picks the
// deserializer based on the response's Content-Type header).
builder.Services.AddMassTransitEnvelopeDeserializer();

// Registers MassTransitEnvelopeSerializer in DI for per-message-type use.
// Activated for CheckOrderStatus via MapSerializer<T,S>() below.
builder.Services.AddMassTransitEnvelopeSerializer();

// ─────────────────────────────────────────────────────────────────────────────
// 4. BareWire transport + topology
// ─────────────────────────────────────────────────────────────────────────────

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    rmq.Host(rabbitMqConnectionString);

    // Request routing: BareWire publishes CheckOrderStatus to the default AMQP exchange ("")
    // — DefaultExchange is "" — with routing key = RequestQueueName. On the default AMQP
    // exchange the routing key equals the queue name, so the request goes straight to the
    // "mt-order-status" queue MassTransit listens on. WITHOUT this mapping the request client
    // would use the default routing key (the type name) and the message would never reach the
    // MassTransit responder -> timeout.
    rmq.MapRoutingKey<CheckOrderStatus>(RequestQueueName);

    // Manual topology — BareWire creates no resources for the MassTransit queue.
    // MassTransit declares the "mt-order-status" queue itself via ReceiveEndpoint below.
    rmq.ConfigureTopology(_ => { });
};

builder.Services.AddBareWireRabbitMq(configureRabbitMq);
builder.Services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(configureRabbitMq);

    // Key element: MapSerializer<CheckOrderStatus, MassTransitEnvelopeSerializer>() tells
    // BareWire to wrap every CheckOrderStatus in a MassTransit envelope. The envelope carries
    // messageId, requestId, responseAddress (= amq.rabbitmq.reply-to), correlationId,
    // messageType and the payload — exactly what MassTransit expects.
    cfg.MapSerializer<CheckOrderStatus, MassTransitEnvelopeSerializer>();
});

// ─────────────────────────────────────────────────────────────────────────────
// 5. MassTransit bus — the real responder (the MassTransit side of the interop)
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<OrderStatusResponder>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(new Uri(mtRabbitUri));

        // ReceiveEndpoint named RequestQueueName — MassTransit listens on this queue.
        // ConfigureConsumeTopology = false: MassTransit does NOT create a fanout exchange for
        // CheckOrderStatus, so BareWire can reach the queue through the default AMQP exchange
        // (routing key = RequestQueueName) without any exchange-to-queue binding.
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

// POST /order-status — sends CheckOrderStatus via the BareWire IRequestClient<T> to the
// MassTransit OrderStatusResponder and returns the Response<OrderStatus>.
//
// Message flow:
//   1. BareWire serializes CheckOrderStatus as a MassTransit envelope (MapSerializer<T,S>).
//   2. The envelope goes to the default AMQP exchange ("") with routing key "mt-order-status".
//   3. MassTransit receives from the "mt-order-status" queue and invokes OrderStatusResponder.Consume().
//   4. RespondAsync() routes the reply via the AMQP ReplyTo header (amq.rabbitmq.reply-to).
//   5. BareWire receives OrderStatus and decodes it via MassTransitEnvelopeDeserializer.
//   6. IRequestClient<CheckOrderStatus>.GetResponseAsync<OrderStatus>() returns the result.
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
.WithSummary("Check an order's status via BareWire->MassTransit request/response.");

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
        Message = "BareWire: sending order-status request {OrderId} via IRequestClient<CheckOrderStatus>")]
    internal static partial void SendingRequest(ILogger logger, string orderId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "BareWire: received response — OrderId={OrderId}, Status={Status}, ProcessedBy={ProcessedBy}")]
    internal static partial void ReceivedResponse(ILogger logger, string orderId, string status, string processedBy);
}
