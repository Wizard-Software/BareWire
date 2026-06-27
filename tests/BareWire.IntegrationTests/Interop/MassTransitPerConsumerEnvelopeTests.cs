using System.Collections.Concurrent;
using System.Diagnostics;
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

// BareWire and MassTransit share several type names. Aliases resolve the ambiguity.
using BwConsumeContextOfMtPingRequest =
    BareWire.Abstractions.ConsumeContext<BareWire.IntegrationTests.Interop.MtPingRequest>;
using BwConsumeContextOfRawNote =
    BareWire.Abstractions.ConsumeContext<BareWire.IntegrationTests.Interop.RawNote>;

namespace BareWire.IntegrationTests.Interop;

// ── Shared message types (file-scoped) ────────────────────────────────────────
// Public so ConsumerInvokerFactory + DI can resolve them at runtime.

/// <summary>Request type for the per-consumer MT envelope acceptance test (18.10).</summary>
public sealed record MtPingRequest(string Payload);

/// <summary>Response type for the per-consumer MT envelope acceptance test (18.10).</summary>
public sealed record MtPingResponse(string Echo, string ProcessedBy);

/// <summary>Raw-JSON message type for the mixed-consumer acceptance test (18.10).</summary>
public sealed record RawNote(string Id, string Text);

// ── Thread-safe sink ──────────────────────────────────────────────────────────

/// <summary>
/// Thread-safe collector shared (as a DI singleton) by both test consumers.
/// Provides a polling barrier so a test can wait deterministically for an exact
/// number of dispatches rather than sleeping a fixed amount.
/// </summary>
public sealed class MixedDeliverySink
{
    private readonly ConcurrentQueue<(string Consumer, object Payload)> _hits = new();

    public void Record(string consumer, object payload) =>
        _hits.Enqueue((consumer, payload));

    public int Count => _hits.Count;

    public IReadOnlyList<(string Consumer, object Payload)> Hits => _hits.ToArray();

    /// <summary>
    /// Polls until at least <paramref name="expected"/> hits are recorded or the timeout elapses.
    /// Returns the final reached state; the caller asserts the exact count after a short
    /// stabilisation window to catch any stray over-delivery still in flight.
    /// </summary>
    public async Task<bool> WaitForCountAsync(int expected, TimeSpan timeout, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (_hits.Count >= expected)
                return true;

            await Task.Delay(50, ct);
        }

        return _hits.Count >= expected;
    }
}

// ── Test consumers ────────────────────────────────────────────────────────────

/// <summary>
/// Marked BareWire consumer that processes <see cref="MtPingRequest"/> from a MassTransit envelope.
/// Registered with <c>ep.Consumer&lt;MtPingResponder, MtPingRequest&gt;(c =&gt; c.UseMassTransitEnvelope())</c>.
///
/// Guard for Test 1 (record-only, no reply context): when <see cref="ShouldRespondAsync"/> is
/// <see langword="false"/>, the consumer only records the message to the shared sink and does NOT
/// call <c>RespondAsync</c>. Calling <c>RespondAsync</c> without a valid <c>responseAddress</c>
/// in the inbound MT envelope falls back to <c>PublishAsync</c>, which may throw when no exchange
/// is configured for the response type (ADR-002 manual topology), causing a nack and unbounded
/// redelivery that would inflate the exact-count beyond 2. The reply path is exercised by Test 2
/// only, where a real MT <c>IRequestClient</c> supplies a <c>responseAddress</c>.
/// </summary>
internal sealed class MtPingResponder(MixedDeliverySink sink) : BareWire.Abstractions.IConsumer<MtPingRequest>
{
    /// <summary>
    /// When <see langword="true"/>, the consumer calls
    /// <see cref="BareWire.Abstractions.ConsumeContext.RespondAsync{T}"/> and releases
    /// <see cref="RespondedSignal"/>. Set to <see langword="false"/> for Test 1 (no reply context)
    /// and <see langword="true"/> for Test 2 (MT request/response with a valid
    /// <c>responseAddress</c>). Volatile so the background consume thread always reads the
    /// value written by the test setup thread.
    /// </summary>
    internal static volatile bool ShouldRespondAsync;

    /// <summary>
    /// Signalled when the responder successfully completes <c>RespondAsync</c>.
    /// Used by Test 2 to verify the MT reply envelope was emitted before awaiting the MT response.
    /// </summary>
    internal static readonly SemaphoreSlim RespondedSignal = new(0, 1);

    public async Task ConsumeAsync(BwConsumeContextOfMtPingRequest context)
    {
        sink.Record(nameof(MtPingResponder), context.Message);

        if (ShouldRespondAsync)
        {
            await context.RespondAsync(
                new MtPingResponse(context.Message.Payload, "BareWire/MtPingResponder"),
                context.CancellationToken);

            RespondedSignal.Release();
        }
    }
}

/// <summary>
/// Unmarked BareWire consumer that processes <see cref="RawNote"/> from a raw JSON delivery.
/// Registered with <c>ep.Consumer&lt;RawNoteConsumer, RawNote&gt;()</c> (no MT envelope overlay).
/// Proves that the unmarked consumer retains raw-first default deserialization when it shares a
/// receive endpoint with a marked consumer (per-consumer D4 deserializer selection).
/// </summary>
internal sealed class RawNoteConsumer(MixedDeliverySink sink) : BareWire.Abstractions.IConsumer<RawNote>
{
    public Task ConsumeAsync(BwConsumeContextOfRawNote context)
    {
        sink.Record(nameof(RawNoteConsumer), context.Message);
        return Task.CompletedTask;
    }
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Acceptance tests (18.10) that prove per-consumer MT envelope opt-in works on a single receive
/// endpoint with mixed consumers on a real RabbitMQ broker provisioned via <see cref="AspireFixture"/>.
///
/// <b>Test 1 — mixed consumers, one queue:</b>
///   A marked consumer (<see cref="MtPingResponder"/>, <c>UseMassTransitEnvelope()</c>) and an
///   unmarked consumer (<see cref="RawNoteConsumer"/>) share one receive endpoint. Two deliveries
///   are published via a raw <c>RabbitMQ.Client</c> channel with publisher confirms: (a) an MT
///   envelope for <see cref="MtPingRequest"/>; (b) a raw JSON delivery for <see cref="RawNote"/>.
///   Each consumer receives only its own format — <c>ReceiveEndpointRunner.ResolverFor(i)</c>
///   returns the MT deserializer for the marked consumer and the default JSON deserializer for the
///   unmarked consumer (D4 per-consumer precedence). Exact-count == 2 guards against over-dispatch.
///
/// <b>Test 2 — request/response with MT reply envelope:</b>
///   A MassTransit <c>IRequestClient{T}</c> sends an MT request to the BareWire endpoint. The
///   marked <see cref="MtPingResponder"/> calls <c>context.RespondAsync</c>, which emits a valid
///   MT reply envelope echoing the <c>requestId</c>. The MT client correlates the response by
///   <c>requestId</c> and returns a typed <see cref="MtPingResponse"/>. A
///   <c>RequestTimeoutException</c> (not a <c>CancellationToken</c> cancel, because the CTS budget
///   of 90 s exceeds the 30 s request timeout) proves missing correlation.
///
/// R1 topology finding: MT's <c>IRequestClient</c> publishes to a <b>fanout exchange</b> named
/// after the endpoint — not via the default AMQP exchange. The topology must declare the fanout
/// exchange + queue + binding (durable=true for both, matching MT's own declaration defaults).
/// </summary>
[Trait("Category", "Integration")]
public sealed class MassTransitPerConsumerEnvelopeTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    private readonly AspireFixture _fixture = fixture;

    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(30);

    // Short window after the expected count is reached, to let any stray over-delivery surface
    // before asserting the EXACT total (guards the disjointness assertion against a false pass).
    private static readonly TimeSpan StabilisationWindow = TimeSpan.FromMilliseconds(400);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds and starts a BareWire bus hosting both <see cref="MtPingResponder"/> (marked,
    /// <c>UseMassTransitEnvelope()</c>) and <see cref="RawNoteConsumer"/> (unmarked) on a single
    /// receive endpoint for <paramref name="queueName"/>. The topology declares a fanout exchange +
    /// queue + binding (R1 pattern) so MassTransit's <c>IRequestClient</c> can publish to the
    /// fanout exchange in Test 2. Using <c>durable=true</c> for both exchange and queue matches
    /// MT's declaration defaults and prevents <c>AMQP PRECONDITION_FAILED</c> on re-declaration.
    /// </summary>
    private async Task<IHost> StartBareWireBusAsync(
        string queueName,
        MixedDeliverySink sink,
        CancellationToken ct)
    {
        string connectionString = _fixture.GetRabbitMqConnectionString();

        Action<BareWire.Abstractions.Configuration.IRabbitMqConfigurator> configureRabbitMq = rmq =>
        {
            rmq.Host(connectionString);

            // R1: MT publishes to a fanout exchange named after the endpoint (not via the default
            // AMQP exchange). Declare the exchange + queue + binding so MT's publish reaches
            // BareWire's consumers. durable=true matches MT's default declaration.
            rmq.ConfigureTopology(t =>
            {
                t.DeclareExchange(queueName, BareWire.Abstractions.ExchangeType.Fanout,
                    durable: true, autoDelete: false);
                t.DeclareQueue(queueName, durable: true, autoDelete: false);
                t.BindExchangeToQueue(queueName, queueName, routingKey: string.Empty);
            });

            // Mixed receive endpoint: marked consumer (MT envelope deserializer, D4) + unmarked
            // consumer (raw-first JSON deserializer) on one shared queue. ResolverFor(i) in
            // ReceiveEndpointRunner returns the MT deserializer only for MtPingResponder.
            rmq.ReceiveEndpoint(queueName, ep =>
            {
                ep.Consumer<MtPingResponder, MtPingRequest>(c => c.UseMassTransitEnvelope());
                ep.Consumer<RawNoteConsumer, RawNote>();
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

                // Register consumers so DI can resolve them per-message (transient).
                services.AddTransient<MtPingResponder>();
                services.AddTransient<RawNoteConsumer>();

                // Shared thread-safe sink — DI singleton injected into both consumers.
                services.AddSingleton(sink);

                // RabbitMQ transport adapter.
                services.AddBareWireRabbitMq(configureRabbitMq);

                // Core bus engine.
                services.AddBareWire(cfg =>
                {
                    // UseRabbitMQ is a deprecated no-op (Feature 15, ADR-028 D4); transport comes
                    // from AddBareWireRabbitMq above. CS0618 suppressed for the coexistence call.
#pragma warning disable CS0618 // Type or member is obsolete
                    cfg.UseRabbitMQ(configureRabbitMq);
#pragma warning restore CS0618 // Type or member is obsolete
                });
            })
            .Build();

        await host.StartAsync(ct);

        // Allow the bus + consumers to fully bind before publishing.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        // Verify the queue exists by passive declare — confirms topology was deployed.
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
    /// MT automatically creates a server-named reply queue for <c>IRequestClient</c> correlation.
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

    // ── Test 1 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Proves that a marked consumer (<c>UseMassTransitEnvelope()</c>) and an unmarked consumer
    /// share one receive endpoint and each receives only its own delivery format (per-consumer D4
    /// deserializer selection via <c>ReceiveEndpointRunner.ResolverFor</c>). Exact-count == 2
    /// guards against over-dispatch (a stray re-delivery would push the count above 2).
    ///
    /// The MT envelope hand-crafted for this test carries NO <c>requestId</c> or
    /// <c>responseAddress</c>, so <see cref="MtPingResponder.ShouldRespondAsync"/> is set to
    /// <see langword="false"/>: the responder only records to the sink and never calls
    /// <c>RespondAsync</c>. Without a reply address, <c>RespondAsync</c> would fall back to
    /// <c>PublishAsync</c>, which may throw when no exchange is configured for the response type
    /// (ADR-002), causing a nack and redelivery loop that would inflate the count beyond 2.
    /// </summary>
    [Fact]
    public async Task MixedConsumers_OneQueue_MarkedReceivesMtEnvelope_UnmarkedReceivesRaw()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string queueName = $"bw-18-10-mixed-{Guid.NewGuid():N}";

        // Guard: record-only for Test 1 — the hand-crafted envelope has no responseAddress.
        MtPingResponder.ShouldRespondAsync = false;

        // Drain the signal in case a prior test run left it in a released state.
        while (MtPingResponder.RespondedSignal.CurrentCount > 0)
            MtPingResponder.RespondedSignal.Wait(0);

        string connectionString = _fixture.GetRabbitMqConnectionString();
        var sink = new MixedDeliverySink();

        IHost bwHost = await StartBareWireBusAsync(queueName, sink, cts.Token);

        try
        {
            // Publish both deliveries via the DEFAULT AMQP exchange (routing key = queue name)
            // with publisher confirms — single channel enforces FIFO order on the broker queue.
            var factory = new ConnectionFactory
            {
                Uri = new Uri(connectionString),
                AutomaticRecoveryEnabled = false,
            };
            await using IConnection conn = await factory.CreateConnectionAsync(cts.Token);
            await using IChannel channel = await conn.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cts.Token);

            // (a) MT envelope for MtPingRequest. ContentType signals MT format to ResolverFor(0).
            //     No requestId/responseAddress — MtPingResponder will not call RespondAsync.
            //     messageType is included for realism but is NOT load-bearing (GAP-1): the
            //     deserializer binds only the inner `message` field; consumer selection comes
            //     from per-consumer registration, not the messageType URN.
            string[] mtMessageType = ["urn:message:BareWire.IntegrationTests.Interop:MtPingRequest"];
            string mtEnvelope = JsonSerializer.Serialize(new
            {
                messageId = Guid.NewGuid().ToString(),
                message = new { Payload = "mt-mixed" },
                messageType = mtMessageType,
            });
            await channel.BasicPublishAsync<BasicProperties>(
                exchange: string.Empty,
                routingKey: queueName,
                mandatory: false,
                basicProperties: new BasicProperties
                {
                    ContentType = "application/vnd.masstransit+json",
                },
                body: Encoding.UTF8.GetBytes(mtEnvelope),
                cancellationToken: cts.Token);

            // (b) Raw JSON for RawNote. AMQP Type → BW-MessageType header → typed dispatch
            //     routes directly to RawNoteConsumer using the default JSON deserializer.
            await channel.BasicPublishAsync<BasicProperties>(
                exchange: string.Empty,
                routingKey: queueName,
                mandatory: false,
                basicProperties: new BasicProperties
                {
                    ContentType = "application/json",
                    Type = "RawNote",
                },
                body: Encoding.UTF8.GetBytes("""{"id":"N-1","text":"raw-note"}"""),
                cancellationToken: cts.Token);

            // Wait (polling barrier) for both consumers to record their respective deliveries.
            bool reached = await sink.WaitForCountAsync(2, DispatchTimeout, cts.Token);
            reached.Should().BeTrue(
                because: "both mixed consumers must receive their respective deliveries within 30s");

            // Stabilisation window: let any stray over-delivery arrive before asserting exact total.
            await Task.Delay(StabilisationWindow, cts.Token);

            sink.Count.Should().Be(2,
                because: "exactly two deliveries must be dispatched — one per consumer, no over-dispatch");

            // Assert: marked consumer received MtPingRequest deserialized from the MT envelope.
            (string Consumer, object Payload) mtHit =
                sink.Hits.Single(h => h.Consumer == nameof(MtPingResponder));
            mtHit.Payload.Should().BeOfType<MtPingRequest>()
                .Which.Payload.Should().Be("mt-mixed",
                    because: "the marked consumer deserializes MtPingRequest from the MT envelope's message field");

            // Assert: unmarked consumer received RawNote deserialized raw-first from the JSON body.
            (string Consumer, object Payload) rawHit =
                sink.Hits.Single(h => h.Consumer == nameof(RawNoteConsumer));
            rawHit.Payload.Should().BeOfType<RawNote>()
                .Which.Should().Be(new RawNote("N-1", "raw-note"),
                    because: "the unmarked consumer deserializes RawNote raw-first from the JSON body");
        }
        finally
        {
            await bwHost.StopAsync(CancellationToken.None);
            bwHost.Dispose();
        }
    }

    // ── Test 2 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Proves that the marked consumer's <c>RespondAsync</c> emits a valid MassTransit reply
    /// envelope with the correct <c>requestId</c>, which the MT <c>IRequestClient</c> correlates
    /// to return a typed <see cref="MtPingResponse"/>. A <c>RequestTimeoutException</c> (not an
    /// <c>OperationCanceledException</c>) signals missing correlation, because the CTS budget of
    /// 90 s exceeds the 30 s request timeout — the non-additive budgets keep the assertion clean.
    /// </summary>
    [Fact]
    public async Task RequestResponse_MarkedConsumer_RespondAsyncEmitsMtReplyEnvelope_MtClientReadsResponse()
    {
        // CTS = 90 s > RequestTimeout (30 s) + ~7 s bus startup, so genuine non-correlation
        // surfaces as RequestTimeoutException rather than OperationCanceledException from the CTS.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string queueName = $"bw-18-10-rr-{Guid.NewGuid():N}";

        // Enable RespondAsync for Test 2; drain the signal from any prior run.
        MtPingResponder.ShouldRespondAsync = true;
        while (MtPingResponder.RespondedSignal.CurrentCount > 0)
            MtPingResponder.RespondedSignal.Wait(0);

        string connectionString = _fixture.GetRabbitMqConnectionString();
        var sink = new MixedDeliverySink();

        // Start BareWire responder first so the queue and fanout exchange exist before MT publishes.
        IHost bwHost = await StartBareWireBusAsync(queueName, sink, cts.Token);
        IHost mtHost = await StartMtRequesterBusAsync(cts.Token);

        try
        {
            // Resolve MT IBus and create a request client targeting the BareWire fanout exchange.
            // R1: MT publishes to a fanout exchange named after the endpoint address (no port,
            // no ?type=queue). The fanout exchange declared by StartBareWireBusAsync is bound
            // to the queue, so MT's publish reaches BareWire's consumer.
            var bus = mtHost.Services.GetRequiredService<IBus>();
            var uri = new Uri(connectionString);
            string vhost = uri.AbsolutePath.Trim('/');
            string vhostSegment = string.IsNullOrEmpty(vhost) ? string.Empty : $"{vhost}/";

            var endpointAddress = new Uri($"rabbitmq://localhost/{vhostSegment}{queueName}");

            IRequestClient<MtPingRequest> client = bus.CreateRequestClient<MtPingRequest>(
                endpointAddress,
                timeout: RequestTimeout.After(s: 30));

            // Act — issue the MT request.
            Task<Response<MtPingResponse>> responseTask = client.GetResponse<MtPingResponse>(
                new MtPingRequest("hello-per-consumer"),
                cts.Token);

            // Wait for BareWire's marked consumer to signal it called RespondAsync.
            bool responded = await MtPingResponder.RespondedSignal
                .WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

            responded.Should().BeTrue(
                because: "MtPingResponder must receive the MT request and call RespondAsync within 20s — " +
                         "if false, MT did not route to the fanout exchange or the exchange→queue binding is missing");

            // Await the MT response — should have arrived since BareWire already responded.
            Response<MtPingResponse> response = await responseTask;

            response.Message.Echo.Should().Be("hello-per-consumer",
                because: "the BareWire consumer echoes the request payload in the MT reply envelope");

            response.Message.ProcessedBy.Should().Be("BareWire/MtPingResponder",
                because: "the response is produced by MtPingResponder");
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
