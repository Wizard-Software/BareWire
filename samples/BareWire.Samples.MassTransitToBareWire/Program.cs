// BareWire.Samples.MassTransitToBareWire — demonstrates per-consumer message-format
// selection: two consumers share ONE receive endpoint (one queue), but one reads the
// MassTransit envelope while the other reads raw JSON. This is the narrowest of the three
// format-selection axes (bus-global, per-endpoint, per-consumer).
//
// Mixed consumers on one queue:
//   - InventoryConsumer opts into the MassTransit envelope PER CONSUMER via
//     Consumer<C,M>(c => c.UseMassTransitEnvelope()). It reads the inbound MT request
//     envelope sent by MassTransit's IRequestClient<CheckInventory> and replies with a
//     conformant MT response envelope (correlated by requestId). Receive AND reply both
//     use the envelope — driven by the per-consumer flag, not a global content-type guess.
//   - ShipmentConsumer takes NO opt-in, so it uses the raw-first default: it consumes a
//     plain-JSON ShipmentNotice (published by BareWire's own IBus) on the same queue and
//     emits a raw ShipmentRecorded event. The two consumers coexist on one endpoint with
//     two different wire formats.
//
//   IMPORTANT — consumer registration order is load-bearing. A raw delivery carries a
//   BW-MessageType header and is dispatched by type (fast path). The MassTransit envelope
//   carries no such header, so it is matched by trying each consumer's deserializer in
//   REGISTRATION ORDER and taking the first that succeeds. InventoryConsumer (the envelope
//   consumer) MUST be registered first; reversing the order would let the raw consumer
//   misparse the envelope. The registration block below keeps InventoryConsumer first.
//
// What this sample also shows:
//   - MT->BareWire request/response (the B3 interop direction), now per-consumer.
//   - The ergonomic per-type publish API: after answering each request, BareWire publishes an
//     InventoryChecked domain event via IBus.PublishAsync<InventoryChecked> with NO exchange or
//     routing key at the call site. Routing is driven entirely by the per-type mapping registered
//     with the "declare + map" shortcut t.DeclareExchange<InventoryChecked>(name, type, ...,
//     routingKey: ...). The equivalent grouped rmq.Publish<InventoryChecked>(p => { p.Exchange(...);
//     p.RoutingKey(...); }) block and the low-level MapExchange<T>/MapRoutingKey<T> primitives are
//     shown side-by-side in comments in the topology section below.
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
using BareWire.Samples.MassTransitToBareWire;
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

// Outbound domain-event routing (the ergonomic per-type publish path this sample showcases).
// After answering the MT request, BareWire publishes an InventoryChecked event to this
// topic exchange with this routing key — see the DeclareExchange<InventoryChecked> call below.
const string EventsExchangeName = "bw-inventory-events";
const string EventsRoutingKey = "inventory.checked";

// Raw mixed-consumer path. ShipmentNotice is published by BareWire's own IBus as raw JSON to
// this fanout exchange, which is ALSO bound to the shared request queue — so the raw delivery
// lands on the same queue as the MassTransit envelope requests and is handled by ShipmentConsumer.
const string ShipmentNoticesExchangeName = "bw-shipment-notices";

// Observable output of the raw round. ShipmentConsumer publishes ShipmentRecorded here (topic)
// so the end-to-end smoke test can bind an observer queue and confirm the raw consumer ran.
const string ShipmentEventsExchangeName = "bw-shipment-events";
const string ShipmentRecordedRoutingKey = "shipment.recorded";

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
        // INBOUND request topology (consume-side): MT publishes here, BareWire consumes.
        // Stays on the NON-generic DeclareExchange — this exchange is a consume target, not a
        // PublishAsync<T> destination, so it must NOT register a per-type send mapping.
        t.DeclareExchange(RequestQueueName, BareWire.Abstractions.ExchangeType.Fanout,
            durable: true, autoDelete: false);
        t.DeclareQueue(RequestQueueName, durable: true, autoDelete: false);
        t.BindExchangeToQueue(RequestQueueName, RequestQueueName, routingKey: string.Empty);

        // ─────────────────────────────────────────────────────────────────────
        // OUTBOUND publish routing — the ergonomic per-type publish API.
        //
        // SHAPE A — "declare + map" shortcut (USED HERE): a single call declares the
        // exchange AND registers the per-type PublishAsync<InventoryChecked> mapping
        // (exchange + routing key). It replaces the older "scattered" recipe of a plain
        // ConfigureTopology DeclareExchange followed by separate MapExchange<T> +
        // MapRoutingKey<T> calls.
        t.DeclareExchange<InventoryChecked>(
            EventsExchangeName, BareWire.Abstractions.ExchangeType.Topic,
            durable: true, autoDelete: false, routingKey: EventsRoutingKey);

        // SHAPE B — grouped per-type send block (EQUIVALENT; shown for didactics).
        // Declare the exchange once in the topology, then group the send routing on the
        // configurator (rmq) — NOT inside ConfigureTopology:
        //
        //     t.DeclareExchange(EventsExchangeName, BareWire.Abstractions.ExchangeType.Topic, durable: true);
        //     // ...then, at the configurator level:
        //     rmq.Publish<InventoryChecked>(p =>
        //     {
        //         p.Exchange(EventsExchangeName);
        //         p.RoutingKey(EventsRoutingKey);
        //     });
        //
        // SHAPE C — low-level primitives the grouped block desugars to:
        //     rmq.MapExchange<InventoryChecked>(EventsExchangeName);
        //     rmq.MapRoutingKey<InventoryChecked>(EventsRoutingKey);
        //
        // All three shapes feed the SAME per-type mapping set (single source of truth).
        // Runtime precedence is unchanged: BW-Exchange header > per-type mapping > DefaultExchange.

        // ─────────────────────────────────────────────────────────────────────
        // RAW mixed-consumer path (the second consumer on the SAME queue).
        //
        // ShipmentNotice is a PublishAsync<T> destination, so it uses the generic
        // "declare + map" shortcut: declare the fanout exchange AND register the per-type
        // send mapping. The exchange is then bound to the SHARED request queue so a raw
        // ShipmentNotice published by BareWire lands on the same queue the MassTransit
        // requests use — proving two consumers with different wire formats share one queue.
        t.DeclareExchange<ShipmentNotice>(
            ShipmentNoticesExchangeName, BareWire.Abstractions.ExchangeType.Fanout,
            durable: true, autoDelete: false, routingKey: string.Empty);
        t.BindExchangeToQueue(ShipmentNoticesExchangeName, RequestQueueName, routingKey: string.Empty);

        // Observable output exchange for the raw round: ShipmentConsumer publishes
        // ShipmentRecorded here (topic) with routing key "shipment.recorded". The smoke test
        // binds an observer queue to assert the raw consumer ran with raw-first JSON.
        t.DeclareExchange<ShipmentRecorded>(
            ShipmentEventsExchangeName, BareWire.Abstractions.ExchangeType.Topic,
            durable: true, autoDelete: false, routingKey: ShipmentRecordedRoutingKey);
    });

    rmq.ReceiveEndpoint(RequestQueueName, ep =>
    {
        // Consumer 1 (MUST be registered FIRST — load-bearing, see header note): opts into the
        // MassTransit envelope per consumer. Reads the inbound MT request envelope and replies
        // with an MT response envelope. The envelope has no BW-MessageType header, so it is
        // matched by trying consumers in registration order; the envelope consumer must be first.
        ep.Consumer<InventoryConsumer, CheckInventory>(c => c.UseMassTransitEnvelope());

        // Consumer 2: NO opt-in => raw-first default. Handles the plain-JSON ShipmentNotice
        // (routed by its BW-MessageType header) on this same queue and replies in raw format.
        ep.Consumer<ShipmentConsumer, ShipmentNotice>();
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
        services.AddTransient<ShipmentConsumer>();
        // Singleton settle signal — ShipmentConsumer marks it when the raw round completes so the
        // driver can deterministically wait before shutting down (avoids a flaky smoke test).
        services.AddSingleton<ShipmentSignal>();
        // Two-call registration path — kept intentionally to demonstrate it still works (non-breaking, ADR-028 E7).
        // Core and transport are registered separately; no Use{Transport} is needed (validation now keys on the
        // ITransportAdapter fact in DI, ADR-028 D5). The single-call BareWire.RabbitMQ bundle is the recommended path.
        services.AddBareWireRabbitMq(configureRabbitMq);
        services.AddBareWire(cfg => { });
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
Console.WriteLine($"  Broker:    {safeRabbitMqUri}");
Console.WriteLine($"  Queue:     {RequestQueueName} (shared by both consumers)");
Console.WriteLine($"  Exchange:  {RequestQueueName} (fanout, durable=true) -> MassTransit envelope consumer");
Console.WriteLine($"  Raw in:    {ShipmentNoticesExchangeName} (fanout, durable=true) -> raw consumer (same queue)");
Console.WriteLine($"  Events:    {EventsExchangeName} (topic, durable=true) routingKey '{EventsRoutingKey}'");
Console.WriteLine($"  Raw out:   {ShipmentEventsExchangeName} (topic, durable=true) routingKey '{ShipmentRecordedRoutingKey}'");
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

// MassTransit's IBus (requester side). Note the fully-qualified BareWire bus below — both
// libraries expose a type named IBus, so `using MassTransit;` makes the bare name resolve to
// MassTransit.IBus here.
IBus bus = mtHost.Services.GetRequiredService<IBus>();

// BareWire's IBus (responder side) — used to publish the InventoryChecked domain event via the
// ergonomic per-type mapping configured above. PublishAsync<InventoryChecked> resolves the target
// exchange (bw-inventory-events) and routing key (inventory.checked) from that mapping.
BareWire.Abstractions.IBus bwBus =
    bwHost.Services.GetRequiredService<BareWire.Abstractions.IBus>();

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

    // Emit a domain event for the processed check. No exchange/routing-key is specified at the
    // call site — the ergonomic per-type mapping drives routing to bw-inventory-events with
    // routing key inventory.checked.
    await bwBus.PublishAsync(
        new InventoryChecked(level.Sku, level.Available, level.ProcessedBy),
        cts.Token);
    Console.WriteLine(
        $"[BareWire] Published InventoryChecked for SKU={level.Sku} -> " +
        $"exchange '{EventsExchangeName}', routingKey '{EventsRoutingKey}'.");
}

Console.WriteLine();
Console.WriteLine("Round-trip complete. Both requests responded successfully.");

// ─────────────────────────────────────────────────────────────────────────────
// 5b. Raw mixed-consumer round — publish a raw ShipmentNotice to the SAME queue
// ─────────────────────────────────────────────────────────────────────────────

// This delivery is plain JSON (raw-first, NO MassTransit envelope). It lands on the same queue
// as the MT requests above but is handled by the raw ShipmentConsumer (matched by its
// BW-MessageType header) — proving two consumers with different wire formats share one endpoint.
ShipmentSignal shipmentSignal = bwHost.Services.GetRequiredService<ShipmentSignal>();

Console.WriteLine();
Console.WriteLine("[BareWire] Publishing raw ShipmentNotice (no envelope) to the shared queue...");
await bwBus.PublishAsync(new ShipmentNotice("SKU-001", Quantity: 7), cts.Token);

// Deterministically wait for the async raw round (consume -> publish ShipmentRecorded) to finish
// before shutting the host down. PublishAsync returns at broker-ack, not after processing, so a
// settle point is required to keep the run (and the smoke test) reliable.
await shipmentSignal.Recorded.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);
Console.WriteLine(
    $"[BareWire] Raw round complete: ShipmentConsumer recorded the shipment and published " +
    $"ShipmentRecorded -> exchange '{ShipmentEventsExchangeName}', routingKey '{ShipmentRecordedRoutingKey}'.");

// ─────────────────────────────────────────────────────────────────────────────
// 6. Graceful shutdown
// ─────────────────────────────────────────────────────────────────────────────

await mtHost.StopAsync(CancellationToken.None);
mtHost.Dispose();

await bwHost.StopAsync(CancellationToken.None);
bwHost.Dispose();

Console.WriteLine("Shutdown complete.");
