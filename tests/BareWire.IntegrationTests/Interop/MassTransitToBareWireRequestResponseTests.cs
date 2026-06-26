using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Interop.MassTransit;
using BareWire.Serialization.Json;
using BareWire.Transport.RabbitMQ;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

// BareWire and MassTransit share several type names. Aliases resolve the ambiguity.
using BwConsumeContextOfT = BareWire.Abstractions.ConsumeContext<BareWire.IntegrationTests.Interop.BwInteropPingRequest>;

namespace BareWire.IntegrationTests.Interop;

// ── Shared message types (file-scoped) ────────────────────────────────────────

/// <summary>Request type for MassTransit→BareWire interop acceptance test (B3).</summary>
internal sealed record BwInteropPingRequest(string Payload);

/// <summary>Response type for MassTransit→BareWire interop acceptance test (B3).</summary>
internal sealed record BwInteropPingResponse(string Echo, string ProcessedBy);

/// <summary>
/// BareWire consumer that echoes the request payload and responds via
/// <c>ConsumeContext.RespondAsync</c>. The MT routing metadata
/// (<c>responseAddress</c> + <c>requestId</c>) is extracted by <c>ConsumerInvokerFactory</c>
/// and set on the context; <c>RespondAsync</c> uses it to route the reply and echo
/// the <c>requestId</c> inside the MT response envelope so the MT
/// <c>IRequestClient</c> can correlate it.
/// </summary>
internal sealed class BwInteropPingResponder : BareWire.Abstractions.IConsumer<BwInteropPingRequest>
{
    /// <summary>
    /// Signalled when the responder successfully calls RespondAsync.
    /// Used by tests to verify BareWire received and responded to the MT request.
    /// </summary>
    internal static readonly SemaphoreSlim RespondedSignal = new(0, 1);

    public async Task ConsumeAsync(BwConsumeContextOfT context)
    {
        await context.RespondAsync(new BwInteropPingResponse(
            Echo: context.Message.Payload,
            ProcessedBy: "BareWire/BwInteropPingResponder"),
            context.CancellationToken);

        RespondedSignal.Release();
    }
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Acceptance tests (B3) that prove MassTransit→BareWire request/response interop works
/// on a real RabbitMQ broker provisioned via <see cref="AspireFixture"/>.
///
/// MassTransit is the requester (<c>IRequestClient{T}</c>); BareWire is the responder
/// (<c>IConsumer{T}</c> calling <c>context.RespondAsync</c>).
///
/// R1 wire-level finding (empirically determined from MT source + integration tests):
/// When MT's <c>IRequestClient{T}</c> sends to a <c>rabbitmq://</c> address, MT publishes to
/// a <b>fanout exchange</b> named after the endpoint (same name as the queue). The address query
/// param <c>?type=queue</c> does NOT mean "route via default AMQP exchange" — it sets the AMQP
/// exchange type to the literal string "queue" (invalid). For MT's send path to reach a specific
/// queue, the topology must include:
/// (1) a fanout exchange named after the queue (durable=true to match MT's declaration defaults),
/// (2) the queue itself (durable=true), and
/// (3) a binding from the exchange to the queue.
///
/// MT does NOT set the AMQP <c>ReplyTo</c> property when using a server-named reply queue —
/// the response address is carried only inside the MT JSON envelope body as <c>responseAddress</c>.
/// BareWire's <c>RespondAsync</c> therefore uses Priority 2 (envelope routing): extracts the
/// last path segment of <c>responseAddress</c> (SEC-1) and sends the MT response envelope to
/// <c>queue://localhost/{replyQueueName}</c> via the default AMQP exchange.
///
/// Hard-stop NOT required: MT does not use <c>amq.rabbitmq.reply-to</c> direct-reply-to —
/// it creates a server-named durable reply queue. The reply is sent on a separate outgoing
/// channel, so there is no channel-reuse constraint.
///
/// Production-code gap found during R1: <c>BareWireSendEndpoint.SendRawAsync</c> did not apply
/// the <c>BW-Exchange=""</c> header for <c>queue://</c> URIs, causing the transport to raise
/// <c>BareWireConfigurationException</c>. Fixed in this PR alongside the acceptance test.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MassTransitToBareWireRequestResponseTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    private readonly AspireFixture _fixture = fixture;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds and starts a BareWire bus hosting <see cref="BwInteropPingResponder"/> on
    /// <paramref name="queueName"/>. The topology declares:
    /// <list type="bullet">
    /// <item>A fanout exchange named <paramref name="queueName"/> (durable=true) — MT publishes
    /// to a fanout exchange when sending to a <c>rabbitmq://</c> URI with that name.</item>
    /// <item>A queue named <paramref name="queueName"/> (durable=true).</item>
    /// <item>A binding from the exchange to the queue.</item>
    /// </list>
    /// Using durable=true for both exchange and queue matches MT's own declaration defaults,
    /// which prevents AMQP PRECONDITION_FAILED from re-declaration with different arguments.
    /// </summary>
    private async Task<IHost> StartBareWireBusAsync(string queueName, CancellationToken ct)
    {
        string connectionString = _fixture.GetRabbitMqConnectionString();

        Action<BareWire.Abstractions.Configuration.IRabbitMqConfigurator> configureRabbitMq = rmq =>
        {
            rmq.Host(connectionString);

            // R1: MT publishes to a fanout exchange named after the endpoint (not via the
            // default AMQP exchange). Declare the exchange + queue + binding so MT's publish
            // reaches BareWire's consumer. Use durable=true to match MT's default declaration.
            rmq.ConfigureTopology(t =>
            {
                t.DeclareExchange(queueName, BareWire.Abstractions.ExchangeType.Fanout,
                    durable: true, autoDelete: false);
                t.DeclareQueue(queueName, durable: true, autoDelete: false);
                t.BindExchangeToQueue(queueName, queueName, routingKey: string.Empty);
            });

            // Receive endpoint: BareWire consumer on the request queue.
            rmq.ReceiveEndpoint(queueName, ep =>
            {
                ep.Consumer<BwInteropPingResponder, BwInteropPingRequest>();
            });
        };

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddConsole();
            })
            .ConfigureServices(services =>
            {
                // MT envelope support — raw-first base + MT overlay.
                services.AddBareWireJsonSerializer();
                services.AddMassTransitEnvelopeSerializer();
                services.AddMassTransitEnvelopeDeserializer();

                // Register the consumer so DI can resolve it per-message.
                services.AddTransient<BwInteropPingResponder>();

                // RabbitMQ transport adapter.
                services.AddBareWireRabbitMq(configureRabbitMq);

                // Core bus with receive endpoint.
                services.AddBareWire(cfg =>
                {
                    // UseRabbitMQ is a deprecated no-op (Feature 15, ADR-028 D4); transport comes from
                    // AddBareWireRabbitMq above. CS0618 suppressed for the coexistence call.
#pragma warning disable CS0618 // Type or member is obsolete
                    cfg.UseRabbitMQ(configureRabbitMq);
#pragma warning restore CS0618 // Type or member is obsolete
                });
            })
            .Build();

        await host.StartAsync(ct);

        // Allow the bus + consumer to fully bind.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        // Verify the queue exists by passive declare.
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(connectionString),
                AutomaticRecoveryEnabled = false,
            };
            await using IConnection verifyConn = await factory.CreateConnectionAsync(ct);
            await using IChannel verifyCh = await verifyConn.CreateChannelAsync(cancellationToken: ct);
            await verifyCh.QueueDeclarePassiveAsync(queueName, ct);
        }

        return host;
    }

    /// <summary>
    /// Builds and starts a MassTransit bus as the request-sending side.
    /// No receive endpoint is configured — MT is used purely as a requester.
    /// MT automatically creates a server-named reply queue for its
    /// <c>IRequestClient</c> correlation.
    /// </summary>
    private async Task<IHost> StartMtRequesterBusAsync(CancellationToken ct)
    {
        string connectionString = _fixture.GetRabbitMqConnectionString();
        var uri = new Uri(connectionString);
        string rabbitUri = $"amqp://{uri.UserInfo}@{uri.Host}:{uri.Port}{uri.AbsolutePath}";

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddConsole();
            })
            .ConfigureServices(services =>
            {
                services.AddMassTransit(x =>
                {
                    x.UsingRabbitMq((_, cfg) =>
                    {
                        cfg.Host(new Uri(rabbitUri));
                        // No receive endpoint — MT is purely a requester here.
                        // MT will create an auto-generated reply queue internally.
                    });
                });
            })
            .Build();

        await host.StartAsync(ct);
        // Give MT time to declare its internal reply queue.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        return host;
    }

    // ── Diagnostic test: verify BareWire consumer loop via raw AMQP ───────────

    /// <summary>
    /// Diagnostic: publishes a raw MT-format JSON message directly to BareWire's queue
    /// via the default AMQP exchange (routing key = queue name). Verifies that BareWire's
    /// consumer loop and deserialization stack are working before exercising MT routing.
    /// </summary>
    [Fact]
    public async Task DiagnosticRawAmqp_BareWireConsumerReceivesMessage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string queueName = $"bw-b3-diag-{Guid.NewGuid():N}";

        // Reset the static signal.
        while (BwInteropPingResponder.RespondedSignal.CurrentCount > 0)
            BwInteropPingResponder.RespondedSignal.Wait(0);

        string connectionString = _fixture.GetRabbitMqConnectionString();

        IHost bwHost = await StartBareWireBusAsync(queueName, cts.Token);

        try
        {
            // Publish a minimal MT-envelope JSON message directly to the BareWire queue
            // via the default AMQP exchange (routing key = queue name). This bypasses MT
            // routing entirely to verify BareWire's consumer loop and deserialization stack.
            var factory = new ConnectionFactory
            {
                Uri = new Uri(connectionString),
                AutomaticRecoveryEnabled = false,
            };
            await using IConnection conn = await factory.CreateConnectionAsync(cts.Token);
            await using IChannel ch = await conn.CreateChannelAsync(cancellationToken: cts.Token);

            // Build a minimal MT envelope with requestId and responseAddress.
            Guid requestId = Guid.NewGuid();
            string fakeReplyQueue = $"bw-b3-diag-reply-{Guid.NewGuid():N}";
            var mtUri = new Uri(connectionString);
            string vhost = mtUri.AbsolutePath.Trim('/');
            string vhostSegment = string.IsNullOrEmpty(vhost) ? string.Empty : $"{vhost}/";
            string responseAddress = $"rabbitmq://{mtUri.Host}:{mtUri.Port}/{vhostSegment}{fakeReplyQueue}";

            string[] messageType = ["urn:message:BareWire.IntegrationTests.Interop:BwInteropPingRequest"];
            string envelope = JsonSerializer.Serialize(new
            {
                messageId = Guid.NewGuid().ToString(),
                requestId = requestId.ToString(),
                responseAddress,
                message = new { Payload = "hello-raw-amqp" },
                messageType,
            });

            byte[] body = Encoding.UTF8.GetBytes(envelope);

            var props = new BasicProperties
            {
                ContentType = "application/vnd.masstransit+json",
                MessageId = Guid.NewGuid().ToString(),
            };

            // Publish via default AMQP exchange (empty string), routing key = queue name.
            await ch.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: queueName,
                mandatory: false,
                basicProperties: props,
                body: body,
                cancellationToken: cts.Token);

            // Wait up to 10 s for BareWire's consumer to signal.
            bool consumed = await BwInteropPingResponder.RespondedSignal
                .WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

            consumed.Should().BeTrue(
                because: "BareWire consumer must receive the raw AMQP message via default exchange " +
                         "and call RespondAsync within 10s");
        }
        finally
        {
            await bwHost.StopAsync(CancellationToken.None);
            bwHost.Dispose();
        }
    }

    // ── Test: Acceptance (B3) ─────────────────────────────────────────────────

    /// <summary>
    /// Acceptance test (B3): MassTransit <c>IRequestClient{T}</c> sends a request to
    /// a BareWire consumer. The BareWire consumer calls <c>context.RespondAsync</c>. The MT
    /// client receives the typed response before the timeout — i.e., <c>GetResponse</c>
    /// does NOT throw <c>RequestTimeoutException</c>.
    ///
    /// Routing: MT publishes to a fanout exchange named <c>queueName</c>; BareWire's topology
    /// binds the queue to that exchange (R1 finding). MT does NOT set the AMQP <c>ReplyTo</c>
    /// property — the response address is in the MT envelope body only. BareWire's
    /// <c>RespondAsync</c> Priority-2 path (envelope routing) extracts the queue name from
    /// <c>responseAddress</c> (SEC-1) and sends the MT reply envelope there directly.
    /// </summary>
    [Fact]
    public async Task GetResponse_FromMassTransitRequester_BareWireResponder_ReturnsResponse()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string queueName = $"bw-b3-interop-{Guid.NewGuid():N}";

        // Reset the static signal before starting the test.
        while (BwInteropPingResponder.RespondedSignal.CurrentCount > 0)
            BwInteropPingResponder.RespondedSignal.Wait(0);

        string connectionString = _fixture.GetRabbitMqConnectionString();

        // Start BareWire bus as the responder first so the queue exists before MT publishes.
        IHost bwHost = await StartBareWireBusAsync(queueName, cts.Token);
        IHost mtHost = await StartMtRequesterBusAsync(cts.Token);

        try
        {
            // Resolve MT IBus and create a request client targeting the BareWire exchange.
            // R1: MT publishes to a fanout exchange named after the endpoint address.
            // Use rabbitmq://localhost/vhost/queueName — MT creates/re-uses the exchange and
            // publishes to it; BareWire's binding routes the message to the queue.
            // Do NOT include the port number — MT uses the host+vhost to identify the logical
            // address, and the port is used only for the broker connection (cfg.Host).
            var bus = mtHost.Services.GetRequiredService<IBus>();
            var uri = new Uri(connectionString);
            string vhost = uri.AbsolutePath.Trim('/');
            string vhostSegment = string.IsNullOrEmpty(vhost) ? string.Empty : $"{vhost}/";

            // rabbitmq://localhost/[vhost/]queueName — no port, no ?type=queue.
            // MT matches this to the configured broker connection and publishes to the exchange.
            var endpointAddress = new Uri($"rabbitmq://localhost/{vhostSegment}{queueName}");

            IRequestClient<BwInteropPingRequest> client =
                bus.CreateRequestClient<BwInteropPingRequest>(
                    endpointAddress,
                    timeout: RequestTimeout.After(s: 30));

            // Act — issue the MT request.
            Task<Response<BwInteropPingResponse>> responseTask =
                client.GetResponse<BwInteropPingResponse>(
                    new BwInteropPingRequest("hello-from-masstransit"),
                    cts.Token);

            // Wait for BareWire's consumer to signal it called RespondAsync.
            bool bwResponded = await BwInteropPingResponder.RespondedSignal
                .WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

            bwResponded.Should().BeTrue(
                because: "BareWire consumer must receive the MT request and call RespondAsync " +
                         "within 20s — if false, MT did not route to the fanout exchange or the " +
                         "exchange→queue binding is missing");

            // Await the MT response — should have arrived since BareWire already responded.
            Response<BwInteropPingResponse> response = await responseTask;

            response.Message.Echo.Should().Be("hello-from-masstransit",
                because: "BareWire consumer echoes the request payload in the response");

            response.Message.ProcessedBy.Should().Be("BareWire/BwInteropPingResponder",
                because: "the response was processed by the BareWire consumer");
        }
        finally
        {
            await bwHost.StopAsync(CancellationToken.None);
            bwHost.Dispose();
            await mtHost.StopAsync(CancellationToken.None);
            mtHost.Dispose();
        }
    }
}
