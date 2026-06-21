using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Interop.MassTransit;
using BareWire.Serialization.Json;
using BareWire.Transport.RabbitMQ;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Xunit;

namespace BareWire.IntegrationTests.Interop;

// ── Shared message types (file-scoped, accessible to MT DI) ──────────────────

/// <summary>Request type for BareWire→MassTransit interop acceptance test (B2 / GH #19).</summary>
internal sealed record MtInteropPingRequest(string Payload);

/// <summary>
/// Response type. <see cref="CorrelationNote"/> captures D2 wire-level observations recorded
/// by the MT responder: which correlation fields MT parsed from the BareWire request envelope.
/// </summary>
internal sealed record MtInteropPingResponse(string Echo, string CorrelationNote);

/// <summary>
/// MassTransit responder: echoes the request payload and captures what correlation fields
/// <see cref="ConsumeContext{T}"/> exposes — used for D2 wire observation.
/// MT parses these from the BareWire envelope (requestId, correlationId, responseAddress).
/// Must be <c>internal</c> so MassTransit DI can instantiate it at runtime.
/// </summary>
internal sealed class MtInteropPingResponder : IConsumer<MtInteropPingRequest>
{
    /// <summary>
    /// Signal set when the responder successfully calls RespondAsync.
    /// Used by tests to verify that MT received AND responded to the BareWire request.
    /// </summary>
    internal static readonly SemaphoreSlim RespondedSignal = new(0, 1);

    public async Task Consume(ConsumeContext<MtInteropPingRequest> context)
    {
        // D2: record which routing fields MT read from the BareWire envelope.
        string note = $"requestId={context.RequestId?.ToString() ?? "null"}," +
                      $"correlationId={context.CorrelationId?.ToString() ?? "null"}," +
                      $"responseAddress={context.ResponseAddress?.ToString() ?? "null"}";

        await context.RespondAsync(new MtInteropPingResponse(context.Message.Payload, note));

        // Signal after a successful RespondAsync — proves MT routed the response.
        RespondedSignal.Release();
    }
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Acceptance tests (B2 — GH #18, GH #19) that prove BareWire→MassTransit request/response
/// interop works on a real RabbitMQ broker provisioned via <see cref="AspireFixture"/>.
///
/// These tests exercise the PRODUCTION code path end-to-end — no test-only address shims.
/// <see cref="RabbitMqRequestClient{TRequest}"/> uses
/// <c>RabbitMqEndpointAddress.BuildReplyToAddress</c> internally to embed
/// <c>amq.rabbitmq.reply-to</c> as the envelope <c>responseAddress</c>, which triggers MT's
/// <c>ReplyToSendEndpoint</c> routing via the default AMQP exchange + AMQP <c>ReplyTo</c>
/// property (actual exclusive reply-queue name).
///
/// D2 objective: capture wire-level facts — which envelope fields MassTransit echoes on the
/// response, and whether AMQP <c>correlation_id</c> is set on the reply — to determine which
/// correlation path fires in <c>RabbitMqRequestClient.TryResolvePending</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MassTransitRequestResponseInteropTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    private readonly AspireFixture _fixture = fixture;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates and starts an MT bus with <see cref="MtInteropPingResponder"/> listening on
    /// <paramref name="queueName"/>. <c>ConfigureConsumeTopology = false</c> prevents MT from
    /// creating a message-type fanout exchange — BareWire reaches the queue via the default
    /// AMQP exchange with <c>routingKey = queueName</c>.
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
                    x.AddConsumer<MtInteropPingResponder>();

                    x.UsingRabbitMq((ctx, cfg) =>
                    {
                        cfg.Host(new Uri(rabbitUri));

                        // Named receive endpoint so BareWire can target it via the default
                        // AMQP exchange (routing key = queue name).
                        // ConfigureConsumeTopology=false: do NOT create message-type fanout
                        // exchange — BareWire sends directly to the queue by routing key.
                        cfg.ReceiveEndpoint(queueName, ep =>
                        {
                            ep.ConfigureConsumeTopology = false;
                            ep.Consumer<MtInteropPingResponder>(ctx);
                        });
                    });
                });
            })
            .Build();

        await host.StartAsync(ct);

        // Allow MT's receive endpoint to fully bind and the consumer to be ready.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        // Verify that MT declared the queue on the broker by passively declaring it.
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
    /// Creates a BareWire <see cref="RabbitMqRequestClient{TRequest}"/> configured with the
    /// production MassTransit envelope serializer/deserializer, targeting the given queue via
    /// the default AMQP exchange (routing key = queue name).
    ///
    /// The production <see cref="MassTransitEnvelopeSerializer"/> is used directly — no
    /// test-only address shims. <see cref="RabbitMqRequestClient{TRequest}.InitializeAsync"/>
    /// calls <c>RabbitMqEndpointAddress.BuildReplyToAddress</c> internally to produce the
    /// correct <c>amq.rabbitmq.reply-to</c> response address.
    /// </summary>
    private async Task<RabbitMqRequestClient<MtInteropPingRequest>> CreateBareWireClientAsync(
        IConnection connection,
        string queueName,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var mtDeserializer = new MassTransitEnvelopeDeserializer();
        var deserializerResolver = new MtDeserializerResolver(mtDeserializer);

        var connectionUri = new Uri(_fixture.GetRabbitMqConnectionString());
        string rawPath = connectionUri.AbsolutePath.TrimStart('/');
        string? vhost = string.IsNullOrEmpty(rawPath) ? null : rawPath;

        // Production serializer — no shim.  InitializeAsync sets _responseAddress via
        // RabbitMqEndpointAddress.BuildReplyToAddress, which produces the amq.rabbitmq.reply-to
        // address that MT's IsReplyToAddress() recognises for direct reply-to routing.
        var client = new RabbitMqRequestClient<MtInteropPingRequest>(
            connection: connection,
            serializer: new MassTransitEnvelopeSerializer(),
            deserializerResolver: deserializerResolver,
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: queueName,
            timeout: timeout,
            connectionUri: connectionUri,
            vhost: vhost);

        await client.InitializeAsync(ct);
        return client;
    }

    private async Task<IConnection> CreateDirectConnectionAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(_fixture.GetRabbitMqConnectionString()),
            AutomaticRecoveryEnabled = false,
        };
        return await factory.CreateConnectionAsync(ct);
    }

    // ── Test 1: Acceptance (GH #19) ──────────────────────────────────────────

    /// <summary>
    /// Acceptance test (GH #19): BareWire request → real MassTransit responder → BareWire
    /// receives the typed response before the timeout, driven entirely by PRODUCTION code.
    ///
    /// The fix is in <c>RabbitMqEndpointAddress.BuildReplyToAddress</c> (called by
    /// <see cref="RabbitMqRequestClient{TRequest}.InitializeAsync"/>): the envelope
    /// <c>responseAddress</c> is set to <c>rabbitmq://host[:port]/[vhost/]amq.rabbitmq.reply-to</c>
    /// instead of the server-named queue URI.  MT's <c>IsReplyToAddress()</c> detects this
    /// suffix and routes the reply via the default AMQP exchange using AMQP <c>ReplyTo</c>
    /// (= actual exclusive reply-queue name) as routing key.
    ///
    /// D2 wire-level observation: <see cref="MtInteropPingResponse.CorrelationNote"/> captures
    /// what MT parsed from the BareWire envelope.  Assertions verify that MT received a valid
    /// <c>responseAddress</c> ending with <c>amq.rabbitmq.reply-to</c> and a non-null
    /// <c>requestId</c> — proving Stage-1 AMQP correlation fires on the response.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_AgainstRealMassTransitResponder_ReturnsResponse()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string queueName = $"bw-mt-interop-{Guid.NewGuid():N}";

        // Reset the static signal before starting the test.
        while (MtInteropPingResponder.RespondedSignal.CurrentCount > 0)
            MtInteropPingResponder.RespondedSignal.Wait(0);

        IHost mtHost = await StartMtBusAsync(queueName, cts.Token);

        try
        {
            await using IConnection connection = await CreateDirectConnectionAsync(cts.Token);
            await using RabbitMqRequestClient<MtInteropPingRequest> client =
                await CreateBareWireClientAsync(connection, queueName, TimeSpan.FromSeconds(30), cts.Token);

            var request = new MtInteropPingRequest("hello-from-barewire");

            // Act — start the request; await the MT signal and then the response.
            // Separating them lets us distinguish routing failure from correlation failure.
            Task<BareWire.Abstractions.Response<MtInteropPingResponse>> responseTask =
                client.GetResponseAsync<MtInteropPingResponse>(request, cts.Token);

            bool mtResponded = await MtInteropPingResponder.RespondedSignal
                .WaitAsync(TimeSpan.FromSeconds(20), cts.Token);

            if (responseTask.IsFaulted)
                await responseTask;

            mtResponded.Should().BeTrue(
                because: "MT responder must receive the BareWire request, process it, " +
                         "and call RespondAsync within 20s — if false, BareWire's envelope " +
                         "did not reach MT's consumer (routing or deserialization failure)");

            BareWire.Abstractions.Response<MtInteropPingResponse> response = await responseTask;

            response.Message.Echo.Should().Be("hello-from-barewire",
                because: "MT responder echoes the request payload");

            // D2 — wire-level assertions.
            string correlationNote = response.Message.CorrelationNote;

            // responseAddress must contain amq.rabbitmq.reply-to — proves production code
            // embedded BuildReplyToAddress output and MT used it to route the response.
            correlationNote.Should().Contain("responseAddress=rabbitmq://",
                because: "MT must have seen a valid responseAddress in the BareWire envelope");
            correlationNote.Should().Contain("amq.rabbitmq.reply-to",
                because: "production BuildReplyToAddress must embed the MT direct reply-to address");

            // requestId non-null — MT parsed it; enables the envelope-requestId fallback path.
            correlationNote.Should().NotContain("requestId=null",
                because: "MT must have parsed a non-null requestId from the BareWire envelope");
        }
        finally
        {
            await mtHost.StopAsync(CancellationToken.None);
            mtHost.Dispose();
        }
    }

    // ── Test 2: TTL (GH #18) ─────────────────────────────────────────────────

    /// <summary>
    /// TTL test (GH #18): verifies that the AMQP <c>expiration</c> property is set on the
    /// published request by intercepting the raw AMQP message before it is consumed.
    ///
    /// Design: a raw RabbitMQ consumer on the target queue intercepts the first message and
    /// captures its AMQP <c>Expiration</c> property. This is deterministic and fast — no
    /// TTL-expiry wait needed. The BareWire request times out (no MT responder) — expected.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_AlwaysSetsAmqpExpiration_OnPublishedRequest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string queueName = $"bw-mt-ttl-{Guid.NewGuid():N}";

        await using IConnection setupConnection = await CreateDirectConnectionAsync(cts.Token);
        await using IChannel setupChannel = await setupConnection
            .CreateChannelAsync(cancellationToken: cts.Token);
        await setupChannel.QueueDeclareAsync(
            queue: queueName,
            durable: false,
            exclusive: false,
            autoDelete: true,
            arguments: null,
            cancellationToken: cts.Token);

        var expirationTcs = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using IChannel consumerChannel = await setupConnection
            .CreateChannelAsync(cancellationToken: cts.Token);

        var rawConsumer = new RabbitMQ.Client.Events.AsyncEventingBasicConsumer(consumerChannel);
        rawConsumer.ReceivedAsync += (_, args) =>
        {
            expirationTcs.TrySetResult(args.BasicProperties.Expiration);
            return Task.CompletedTask;
        };
        await consumerChannel.BasicConsumeAsync(
            queue: queueName,
            autoAck: true,
            consumer: rawConsumer,
            cancellationToken: cts.Token);

        await using IConnection requestConnection = await CreateDirectConnectionAsync(cts.Token);
        var shortTimeout = TimeSpan.FromSeconds(5);
        await using RabbitMqRequestClient<MtInteropPingRequest> client =
            await CreateBareWireClientAsync(requestConnection, queueName, shortTimeout, cts.Token);

        var requestTask = client.GetResponseAsync<MtInteropPingResponse>(
            new MtInteropPingRequest("ttl-probe"), cts.Token);

        using var captureCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        string? capturedExpiration = null;
        try
        {
            capturedExpiration = await expirationTcs.Task.WaitAsync(captureCts.Token);
        }
        catch (OperationCanceledException)
        {
            // capture timed out — test will fail on assertion below
        }

        capturedExpiration.Should().NotBeNullOrEmpty(
            because: "BareWire must set AMQP 'expiration' on every published request (GH #18)");
        bool isValidMs = long.TryParse(capturedExpiration, out long expirationMs) && expirationMs > 0;
        isValidMs.Should().BeTrue(
            because: $"AMQP expiration must be a positive integer in milliseconds; got '{capturedExpiration}'");

        expirationMs.Should().BeCloseTo((long)shortTimeout.TotalMilliseconds, delta: 1000,
            because: "expiration should match the configured client timeout");

        try { await requestTask; }
        catch (BareWire.Abstractions.Exceptions.RequestTimeoutException) { /* expected */ }
    }

    // ── Private: deserializer helper ──────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="IDeserializerResolver"/> that returns the MassTransit envelope
    /// deserializer for its content-type and falls back to raw JSON for everything else.
    /// </summary>
    private sealed class MtDeserializerResolver(MassTransitEnvelopeDeserializer mtDeserializer)
        : IDeserializerResolver
    {
        private const string MtContentType = "application/vnd.masstransit+json";
        private readonly Abstractions.Serialization.IMessageDeserializer _fallback =
            new SystemTextJsonRawDeserializer();

        public Abstractions.Serialization.IMessageDeserializer Resolve(string? contentType)
            => string.Equals(contentType, MtContentType, StringComparison.OrdinalIgnoreCase)
                ? mtDeserializer
                : _fallback;
    }
}
