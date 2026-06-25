using AwesomeAssertions;
using BareWire.Interop.MassTransit;
using BareWire.Serialization.Json;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Internal;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Xunit;

// BareWire and MassTransit share several type names. Aliases resolve the ambiguity.
using MtIConsumerOfT = MassTransit.IConsumer<BareWire.IntegrationTests.Interop.PublishStylePingRequest>;
using MtConsumeContextOfT = MassTransit.ConsumeContext<BareWire.IntegrationTests.Interop.PublishStylePingRequest>;

namespace BareWire.IntegrationTests.Interop;

// ── Shared message types (file-scoped) ────────────────────────────────────────

/// <summary>Request type for BareWire publish-style → MassTransit interop test (ADR-027 D8(b)).</summary>
internal sealed record PublishStylePingRequest(string Payload);

/// <summary>
/// Response type. <see cref="CorrelationNote"/> captures wire-level observations from the MT
/// responder: which correlation fields MassTransit parsed from the BareWire publish-style envelope.
/// </summary>
internal sealed record PublishStylePingResponse(string Echo, string CorrelationNote);

/// <summary>
/// MassTransit responder for publish-style interop: echoes the request payload, captures
/// correlation wire-level observations, calls <c>RespondAsync</c>, and signals the test.
/// Must be <c>internal</c> so MassTransit DI can instantiate it at runtime.
/// </summary>
internal sealed class PublishStylePingResponder : MtIConsumerOfT
{
    /// <summary>
    /// Signal set when the responder successfully calls RespondAsync.
    /// Isolated from <see cref="MtInteropPingResponder.RespondedSignal"/> — no shared state.
    /// </summary>
    internal static readonly SemaphoreSlim RespondedSignal = new(0, 1);

    public async Task Consume(MtConsumeContextOfT context)
    {
        // Capture wire-level routing fields MT read from the BareWire envelope.
        string note = $"requestId={context.RequestId?.ToString() ?? "null"}," +
                      $"correlationId={context.CorrelationId?.ToString() ?? "null"}," +
                      $"responseAddress={context.ResponseAddress?.ToString() ?? "null"}";

        await context.RespondAsync(new PublishStylePingResponse(context.Message.Payload, note));

        // Signal after a successful RespondAsync — proves MT received and responded.
        RespondedSignal.Release();
    }
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Integration test proving BareWire-requester in publish-style mode reaches a MassTransit
/// responder bound to a per-type fanout exchange, and the response correlates (ADR-027
/// Enforcement :211).
///
/// This is the REVERSE of the send-style test in <see cref="MassTransitRequestResponseInteropTests"/>
/// (ADR-027 D8(b)): the MT responder is connected to the per-type fanout exchange
/// <c>Namespace:TypeName</c> via an explicit queue binding (ADR-027 D8(b) fallback — see
/// <see cref="StartMtBusAsync"/>). The BareWire requester is configured via
/// <c>rmq.PublishRequest&lt;T&gt;()</c> in the production DI path
/// (<c>AddBareWireRabbitMq</c> + <c>AddBareWire</c>), which routes the request through
/// <c>RabbitMqRequestClientFactory.ResolveDispatch&lt;T&gt;()</c> to the per-type fanout exchange
/// with an empty routing key.
///
/// Exactly ONE <c>[Fact]</c> is allowed in this class: the fanout exchange name is a fixed
/// <c>Namespace:TypeName</c> (not a Guid), so a second <c>[Fact]</c> on the same request type
/// under the shared <see cref="AspireFixture"/> would reuse the same exchange and risk
/// interference (PERF-1). The responder queue name is Guid-suffixed to avoid queue collisions
/// across runs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PublishStyleMassTransitInteropTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    private readonly AspireFixture _fixture = fixture;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates and starts an MT bus with <see cref="PublishStylePingResponder"/> listening on
    /// <paramref name="queueName"/>. Uses <c>ConfigureConsumeTopology = false</c> and manually
    /// declares the per-type fanout exchange + queue binding (ADR-027 D8(b) fallback), because
    /// MT's <c>ConfigureConsumeTopology = true</c> creates an intermediate endpoint exchange chain
    /// (per-type → endpoint exchange → queue) that prevents BareWire from delivering directly to
    /// the queue. With <c>ConfigureConsumeTopology = false</c> MT only creates the queue and an
    /// endpoint exchange; we add the per-type fanout exchange and bind the queue to it manually.
    /// </summary>
    private async Task<IHost> StartMtBusAsync(string queueName, CancellationToken ct)
    {
        string connectionString = _fixture.GetRabbitMqConnectionString();
        var uri = new Uri(connectionString);
        string rabbitUri = $"amqp://{uri.UserInfo}@{uri.Host}:{uri.Port}{uri.AbsolutePath}";

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddMassTransit(x =>
                {
                    x.AddConsumer<PublishStylePingResponder>();

                    x.UsingRabbitMq((ctx, cfg) =>
                    {
                        cfg.Host(new Uri(rabbitUri));

                        cfg.ReceiveEndpoint(queueName, ep =>
                        {
                            // Prevents MT from creating the per-type fanout exchange automatically.
                            // BareWire must target the exchange by name; we bind the queue manually below.
                            ep.ConfigureConsumeTopology = false;
                            ep.Consumer<PublishStylePingResponder>(ctx);
                        });
                    });
                });
            })
            .Build();

        await host.StartAsync(ct);

        // Allow MT to fully declare the queue and start the consumer.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        // Manually declare the per-type fanout exchange (Namespace:TypeName) and bind MT's queue
        // to it. BareWire's topology deploy uses the same args (durable=false, autoDelete=false),
        // so the redeclaration during StartBareWireRequesterBusAsync is idempotent — no
        // PRECONDITION_FAILED. RequestExchangeNameFormatter.Format<T>() produces the exact name
        // that MassTransit would use for ConfigureConsumeTopology=true, which is what proves the
        // formatter implements ADR-027 D8 correctly.
        string expectedExchangeName = RequestExchangeNameFormatter.Format<PublishStylePingRequest>();
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(connectionString),
                AutomaticRecoveryEnabled = false,
            };
            await using IConnection setupConn = await factory.CreateConnectionAsync(ct);
            await using IChannel setupCh = await setupConn.CreateChannelAsync(cancellationToken: ct);

            await setupCh.ExchangeDeclareAsync(
                exchange: expectedExchangeName,
                type: "fanout",
                durable: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: ct);

            await setupCh.QueueBindAsync(
                queue: queueName,
                exchange: expectedExchangeName,
                routingKey: string.Empty,
                arguments: null,
                cancellationToken: ct);

            // Sanity-check: verify MT's queue was declared by passively asserting its existence.
            await setupCh.QueueDeclarePassiveAsync(queueName, ct);
        }

        return host;
    }

    /// <summary>
    /// Builds and starts a BareWire host configured as a pure publish-style requester. No receive
    /// endpoint is registered — BareWire is used only to send requests via the production DI path.
    /// <c>rmq.PublishRequest&lt;T&gt;()</c> registers the mapping in
    /// <see cref="BareWire.Transport.RabbitMQ.Internal.RabbitMqTransportOptions.PublishRequestMappings"/>,
    /// and <c>RabbitMqRequestClientFactory.ResolveDispatch&lt;T&gt;()</c> reads it to route
    /// requests to the per-type fanout exchange with an empty routing key.
    /// <c>cfg.MapSerializer&lt;T, MassTransitEnvelopeSerializer&gt;()</c> ensures the serializer
    /// resolver returns <see cref="MassTransitEnvelopeSerializer"/> for this request type — without
    /// it the factory would use the default raw-JSON serializer and MassTransit would silently
    /// reject the message (wrong content-type, no envelope).
    /// </summary>
    private async Task<IHost> StartBareWireRequesterBusAsync(CancellationToken ct)
    {
        string connectionString = _fixture.GetRabbitMqConnectionString();
        string exchangeName = RequestExchangeNameFormatter.Format<PublishStylePingRequest>();

        Action<BareWire.Abstractions.Configuration.IRabbitMqConfigurator> configureRabbitMq = rmq =>
        {
            rmq.Host(connectionString);

            // Declare the per-type fanout exchange so ValidatePublishRequestMappings passes
            // (AutoDeclare=false is the default for PublishRequest<T>()). Use durable=false,
            // autoDelete=false to match the declaration made in StartMtBusAsync — any arg mismatch
            // causes PRECONDITION_FAILED during BareWire topology deploy (R1 mitigation).
            rmq.ConfigureTopology(t =>
            {
                t.DeclareExchange(exchangeName, BareWire.Abstractions.ExchangeType.Fanout,
                    durable: false, autoDelete: false);
            });

            // Register publish-style routing: ResolveDispatch<T>() returns
            // (serializer, exchangeName=Namespace:TypeName, routingKey="", strict=false).
            rmq.PublishRequest<PublishStylePingRequest>();
        };

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                // Raw-first base + MassTransit envelope overlay (serializer + deserializer).
                services.AddBareWireJsonSerializer();
                services.AddMassTransitEnvelopeSerializer();
                services.AddMassTransitEnvelopeDeserializer();

                // RabbitMQ transport adapter (registers ITransportAdapter + IRequestClientFactory).
                services.AddBareWireRabbitMq(configureRabbitMq);

                // Core bus (registers IBus).
                // MapSerializer wires MassTransitEnvelopeSerializer into the ISerializerResolver
                // used by RabbitMqRequestClientFactory. Without it, Resolve<PublishStylePingRequest>()
                // returns the default raw-JSON serializer, producing content-type=application/json —
                // MassTransit silently rejects the message (no envelope, no messageType array).
                services.AddBareWire(cfg =>
                {
                    cfg.UseRabbitMQ(configureRabbitMq);
                    cfg.MapSerializer<PublishStylePingRequest, MassTransitEnvelopeSerializer>();
                });
            })
            .Build();

        await host.StartAsync(ct);
        return host;
    }

    // ── Test ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proves that a BareWire publish-style request reaches a MassTransit responder bound to the
    /// per-type fanout exchange, and the response correlates correctly (ADR-027 Enforcement :211).
    ///
    /// Split-wait pattern: first await the MT signal (proves routing through the fanout exchange),
    /// then await the BareWire response (proves correlation). Distinguishing the two failures makes
    /// it easier to diagnose routing vs. correlation problems.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_PublishStyle_AgainstMassTransitResponderOnFanoutExchange_ReturnsResponse()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        // Guid-suffixed queue name prevents queue collisions across parallel runs.
        // The fanout exchange name is fixed (Namespace:TypeName) and is collision-free
        // as long as only one [Fact] in this class uses PublishStylePingRequest (PERF-1).
        string queueName = $"bw-ps-mt-interop-{Guid.NewGuid():N}";
        string payload = "hello-publish-style";

        // Reset the static signal before starting.
        while (PublishStylePingResponder.RespondedSignal.CurrentCount > 0)
            PublishStylePingResponder.RespondedSignal.Wait(0);

        // Start the MT responder first so the fanout exchange and queue binding exist before
        // BareWire publishes (mandatory:false → silent drop if no binding exists).
        IHost mtHost = await StartMtBusAsync(queueName, cts.Token);
        IHost bwHost = await StartBareWireRequesterBusAsync(cts.Token);

        try
        {
            // Resolve IBus and get an IRequestClient<T> through the production factory path.
            // ResolveDispatch<T>() reads PublishRequestMappings → (fanout exchange, routingKey="").
            BareWire.Abstractions.IBus bus =
                bwHost.Services.GetRequiredService<BareWire.Abstractions.IBus>();

            BareWire.Abstractions.IRequestClient<PublishStylePingRequest> requestClient =
                await bus.CreateRequestClientAsync<PublishStylePingRequest>(cts.Token);

            // Fanout sniffer: an exclusive auto-delete queue bound to the per-type exchange.
            // Receives a copy of every message BareWire publishes to the exchange — allows fast-fail
            // assertion that BareWire is actually publishing to the right exchange, independently of
            // whether MT dispatches the message to its consumer. Does NOT consume from MT's queue,
            // so it does not interfere with MT's consumer.
            string exchangeName = RequestExchangeNameFormatter.Format<PublishStylePingRequest>();
            var snifferTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            string connectionString = _fixture.GetRabbitMqConnectionString();
            var snifferFactory = new ConnectionFactory
            {
                Uri = new Uri(connectionString),
                AutomaticRecoveryEnabled = false,
            };
            await using IConnection snifferConn = await snifferFactory.CreateConnectionAsync(cts.Token);
            await using IChannel snifferCh = await snifferConn.CreateChannelAsync(cancellationToken: cts.Token);
            string snifferQueue = $"bw-ps-sniffer-{Guid.NewGuid():N}";
            await snifferCh.QueueDeclareAsync(
                queue: snifferQueue, durable: false, exclusive: true, autoDelete: true,
                arguments: null, cancellationToken: cts.Token);
            await snifferCh.QueueBindAsync(
                queue: snifferQueue, exchange: exchangeName, routingKey: string.Empty,
                arguments: null, cancellationToken: cts.Token);
            var snifferConsumer = new RabbitMQ.Client.Events.AsyncEventingBasicConsumer(snifferCh);
            snifferConsumer.ReceivedAsync += (_, args) =>
            {
                string body = System.Text.Encoding.UTF8.GetString(args.Body.ToArray());
                snifferTcs.TrySetResult(body[..Math.Min(200, body.Length)]);
                return Task.CompletedTask;
            };
            await snifferCh.BasicConsumeAsync(
                queue: snifferQueue, autoAck: true, consumer: snifferConsumer,
                cancellationToken: cts.Token);

            var request = new PublishStylePingRequest(payload);

            // Act — start the request; separate the two awaits to distinguish failure modes.
            Task<BareWire.Abstractions.Response<PublishStylePingResponse>> responseTask =
                requestClient.GetResponseAsync<PublishStylePingResponse>(request, cts.Token);

            // Fast-fail: confirm BareWire published to the per-type fanout exchange within 5s.
            // If this fails, the problem is in BareWire's publish path (exchange name, routing key,
            // or serializer not producing the MT envelope). If it passes but MT never responds, the
            // problem is in MT's dispatch pipeline (message type filter, deserialization).
            using var snifferCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string? sniffedBody = null;
            try { sniffedBody = await snifferTcs.Task.WaitAsync(snifferCts.Token); }
            catch (OperationCanceledException) { }
            sniffedBody.Should().NotBeNull(
                because: $"BareWire must publish the request to exchange '{exchangeName}' using the " +
                         $"MT envelope serializer (content-type=application/vnd.masstransit+json); " +
                         $"if null, BareWire did not publish to the per-type fanout exchange");

            // (1) Wait for MT to signal it received and responded to the request.
            bool mtResponded = await PublishStylePingResponder.RespondedSignal
                .WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

            if (responseTask.IsFaulted)
                await responseTask;

            // Assert (1): proves the request reached MT through the per-type fanout exchange.
            // If BareWire published to the wrong exchange or with wrong serializer/routing key,
            // MT would never receive the message and this assertion fails — it is the core proof
            // of the publish-style path (contrast: send-style uses ConfigureConsumeTopology=false).
            mtResponded.Should().BeTrue(
                because: $"the MT responder must receive the BareWire publish-style request through " +
                         $"the per-type fanout exchange and call RespondAsync within 20s; " +
                         $"if false, the request did not route through the fanout exchange to the " +
                         $"bound queue (sniffer body prefix: " +
                         $"'{sniffedBody?[..Math.Min(50, sniffedBody.Length)] ?? "(null)"}')");

            BareWire.Abstractions.Response<PublishStylePingResponse> response = await responseTask;

            // Assert (2): round-trip payload echoed — proves full request/response correlation.
            response.Message.Echo.Should().Be(payload,
                because: "MT responder echoes the request payload in the response");

            // Assert (3): wire-level correlation (ADR-021/022) — unchanged under publish-style.
            string correlationNote = response.Message.CorrelationNote;

            correlationNote.Should().Contain("responseAddress=rabbitmq://",
                because: "MT must have seen a valid rabbitmq:// responseAddress in the BareWire envelope");
            correlationNote.Should().Contain("amq.rabbitmq.reply-to",
                because: "BareWire must embed the direct reply-to address so MT routes the response back");
            correlationNote.Should().NotContain("requestId=null",
                because: "MT must have parsed a non-null requestId from the BareWire envelope");

            // Assert (4): formatter parity — the exchange name BareWire uses matches the fixed convention.
            exchangeName.Should().Be(
                $"{typeof(PublishStylePingRequest).Namespace}:{typeof(PublishStylePingRequest).Name}",
                because: "RequestExchangeNameFormatter must produce the standard Namespace:TypeName " +
                         "string that MassTransit uses for per-type fanout exchanges; a formatter " +
                         "regression would silently misdirect publish-style requests");
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
