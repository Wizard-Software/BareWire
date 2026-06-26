// BareWire.Samples.MassTransitToBareWire — demonstrates the MassTransit->BareWire
// request/response scenario: MassTransit sends a request via IRequestClient<T> to a
// BareWire consumer, which handles it and replies via context.RespondAsync.
//
// What this sample shows:
//   - MT->BareWire request/response (the B3 interop direction).
//   - R1 topology finding: MT publishes to a fanout exchange named after the endpoint.
//     BareWire's topology must declare that exchange (durable=true), the queue, and a
//     binding between them so MT's publish reaches BareWire's consumer.
//   - MT does NOT set the AMQP ReplyTo property when using a server-named reply queue —
//     the responseAddress lives only in the MT JSON envelope body. BareWire's RespondAsync
//     uses Priority-2 (envelope routing): extracts the reply queue name (SEC-1) and sends
//     a conformant MT response envelope back to that queue via the AMQP default exchange.
//   - Production-code fix included: BareWireSendEndpoint.SendRawAsync now correctly sets
//     BW-Exchange="" for queue:// URIs, matching the behaviour of SendAsync.
//
// Architecture:
//
//   MassTransit IRequestClient<CheckInventory>
//     -> publishes to exchange "bw-inventory-check" (fanout, durable=true)
//        Content-Type: application/vnd.masstransit+json
//        responseAddress: rabbitmq://host/vhost/<auto-reply-queue> (in envelope body)
//     -> exchange binding -> queue "bw-inventory-check"
//     -> BareWire InventoryConsumer.ConsumeAsync()
//         -> context.RespondAsync(new InventoryLevel(...))
//           -> RespondAsync Priority-2: sends MT response envelope to reply queue
//     -> MassTransit receives InventoryLevel on its auto-reply queue
//     -> Response<InventoryLevel> returned to caller
//
// Runtime requirements:
//   - A RabbitMQ broker (default: amqp://guest:guest@localhost:5672/).
//   Override via RABBITMQ_CONNECTIONSTRING environment variable or the
//   ConnectionStrings__rabbitmq configuration key.

using BareWire;
using BareWire.Abstractions.Configuration;
using BareWire.Interop.MassTransit;
using BareWire.Samples.MassTransitToBareWire.Consumers;
using BareWire.Samples.MassTransitToBareWire.Messages;
using BareWire.Serialization.Json;
using BareWire.Transport.RabbitMQ;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ─────────────────────────────────────────────────────────────────────────────
// 1. Configuration
// ─────────────────────────────────────────────────────────────────────────────

string rabbitMqConnectionString =
    Environment.GetEnvironmentVariable("RABBITMQ_CONNECTIONSTRING")
    ?? "amqp://guest:guest@localhost:5672/";

Uri rabbitMqUri = new(rabbitMqConnectionString);

// MassTransit expects an amqp://user:pass@host:port/vhost URI (no trailing slash issues).
string mtRabbitUri = $"amqp://{rabbitMqUri.UserInfo}@{rabbitMqUri.Host}:{rabbitMqUri.Port}{rabbitMqUri.AbsolutePath}";

// MT request-client address: rabbitmq://localhost/[vhost/]queueName
// No port, no ?type=queue — MT publishes to a fanout exchange with the endpoint name.
string vhost = rabbitMqUri.AbsolutePath.Trim('/');
string vhostSegment = string.IsNullOrEmpty(vhost) ? string.Empty : $"{vhost}/";
const string RequestQueueName = "bw-inventory-check";
Uri mtEndpointAddress = new($"rabbitmq://localhost/{vhostSegment}{RequestQueueName}");

// ─────────────────────────────────────────────────────────────────────────────
// 2. BareWire host — responder side
// ─────────────────────────────────────────────────────────────────────────────

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    rmq.Host(rabbitMqConnectionString);

    // R1 topology: MT publishes to a fanout exchange named after the endpoint address.
    // BareWire must declare the exchange (durable=true to match MT's declaration defaults),
    // the queue, and a binding so MT's message reaches BareWire's consumer.
    // Using durable=true for both prevents AMQP PRECONDITION_FAILED if MT re-declares them.
    rmq.ConfigureTopology(t =>
    {
        t.DeclareExchange(RequestQueueName, BareWire.Abstractions.ExchangeType.Fanout,
            durable: true, autoDelete: false);
        t.DeclareQueue(RequestQueueName, durable: true, autoDelete: false);
        t.BindExchangeToQueue(RequestQueueName, RequestQueueName, routingKey: string.Empty);
    });

    rmq.ReceiveEndpoint(RequestQueueName, ep =>
    {
        ep.Consumer<InventoryConsumer, CheckInventory>();
    });
};

IHost bwHost = Host.CreateDefaultBuilder()
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Warning);
        logging.AddConsole();
    })
    .ConfigureServices(services =>
    {
        // MT envelope serializer and deserializer — needed so BareWire decodes inbound MT
        // envelopes and routes responses back in MT-conformant envelope format.
        services.AddBareWireJsonSerializer();
        services.AddMassTransitEnvelopeSerializer();
        services.AddMassTransitEnvelopeDeserializer();

        services.AddTransient<InventoryConsumer>();
        services.AddBareWireRabbitMq(configureRabbitMq);
        services.AddBareWire(cfg =>
        {
            // UseRabbitMQ is a deprecated no-op (Feature 15, ADR-028 D4); transport comes from AddBareWireRabbitMq.
            // Migration to AddBareWireWithRabbitMq is task 15.11; CS0618 suppressed here to keep the build green.
#pragma warning disable CS0618 // Type or member is obsolete
            cfg.UseRabbitMQ(configureRabbitMq);
#pragma warning restore CS0618 // Type or member is obsolete
        });
    })
    .Build();

// ─────────────────────────────────────────────────────────────────────────────
// 3. MassTransit host — requester side
// ─────────────────────────────────────────────────────────────────────────────

IHost mtHost = Host.CreateDefaultBuilder()
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Warning);
        logging.AddConsole();
    })
    .ConfigureServices(services =>
    {
        services.AddMassTransit(x =>
        {
            x.UsingRabbitMq((_, cfg) =>
            {
                cfg.Host(new Uri(mtRabbitUri));
                // No receive endpoint needed — MT creates a server-named reply queue internally.
            });
        });
    })
    .Build();

// ─────────────────────────────────────────────────────────────────────────────
// 4. Start both sides and run the request/response round-trip
// ─────────────────────────────────────────────────────────────────────────────

// SEC-3: print only host:port/vhost — never echo credentials to stdout.
string safeRabbitMqUri = rabbitMqUri.GetComponents(
    UriComponents.Host | UriComponents.Port | UriComponents.Path,
    UriFormat.Unescaped);

Console.WriteLine("BareWire.Samples.MassTransitToBareWire starting...");
Console.WriteLine($"  Broker:   {safeRabbitMqUri}");
Console.WriteLine($"  Queue:    {RequestQueueName}");
Console.WriteLine($"  Exchange: {RequestQueueName} (fanout, durable=true)");
Console.WriteLine();

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

await bwHost.StartAsync(cts.Token);
Console.WriteLine("[BareWire] Consumer started, queue+exchange+binding deployed.");

await mtHost.StartAsync(cts.Token);
Console.WriteLine("[MassTransit] Bus started, reply queue created.");

// Allow topology to fully propagate.
await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);

// ─────────────────────────────────────────────────────────────────────────────
// 5. Send two requests via MT IRequestClient<T>
// ─────────────────────────────────────────────────────────────────────────────

IBus bus = mtHost.Services.GetRequiredService<IBus>();

string[] skus = ["SKU-001", "SKU-999"];

foreach (string sku in skus)
{
    Console.WriteLine($"[MassTransit] Sending CheckInventory request for SKU={sku}...");

    IRequestClient<CheckInventory> client = bus.CreateRequestClient<CheckInventory>(
        mtEndpointAddress,
        timeout: RequestTimeout.After(s: 15));

    Response<InventoryLevel> response = await client.GetResponse<InventoryLevel>(
        new CheckInventory(sku), cts.Token);

    InventoryLevel level = response.Message;
    Console.WriteLine(
        $"[MassTransit] Response received: Sku={level.Sku}, Available={level.Available}, " +
        $"ProcessedBy={level.ProcessedBy}");
}

Console.WriteLine();
Console.WriteLine("Round-trip complete. Both requests responded successfully.");

// ─────────────────────────────────────────────────────────────────────────────
// 6. Graceful shutdown
// ─────────────────────────────────────────────────────────────────────────────

await mtHost.StopAsync(CancellationToken.None);
mtHost.Dispose();

await bwHost.StopAsync(CancellationToken.None);
bwHost.Dispose();

Console.WriteLine("Shutdown complete.");
