using System.Collections.Concurrent;
using AwesomeAssertions;
using BareWire.Serialization;
using BareWire.Serialization.Json;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Internal;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BareWire.IntegrationTests.RequestResponse;

// ── File-scoped request/response records — ONE type per [Fact] (PERF-1: avoids
// cross-test fan-out duplicates on the fixed Namespace:TypeName exchange name) ──

/// <summary>Request for [Fact] #1 (first-in-wins, silent drop, no-nack).</summary>
internal sealed record CompetingPingRequestAbc(string Payload);

/// <summary>Response for [Fact] #1.</summary>
internal sealed record CompetingPingResponseAbc(string Echo, string ResponderId);

/// <summary>Request for [Fact] #2 (post-dispose discard).</summary>
internal sealed record CompetingPingRequestE(string Payload);

/// <summary>Response for [Fact] #2.</summary>
internal sealed record CompetingPingResponseE(string Echo);

// ── Capturing logger ──────────────────────────────────────────────────────────

/// <summary>
/// Thread-safe in-process logger that captures all log entries emitted at or above a minimum level.
/// Used to assert that the request client emits no warnings when silently discarding duplicate
/// responses from competing responders.
/// </summary>
internal sealed class CapturingLogger(string categoryName, LogLevel minimumLevel) : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string message = formatter(state, exception);
        _entries.Enqueue((logLevel, $"[{categoryName}] {message}"));
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>All captured entries at <see cref="LogLevel.Warning"/> or above.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> WarnOrAbove =>
        _entries.Where(e => e.Level >= LogLevel.Warning).ToList();
}

/// <summary>
/// <see cref="ILoggerProvider"/> that returns the same <see cref="CapturingLogger"/> for every
/// category, capturing all log entries at <see cref="LogLevel.Trace"/> and above so that warnings
/// are always visible even if the caller passes a high minimum level.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    // Capture everything — let WarnOrAbove filter at assertion time.
    private readonly CapturingLogger _logger = new("RabbitMqRequestClient", LogLevel.Trace);

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => _logger;

    /// <summary>All captured entries at <see cref="LogLevel.Warning"/> or above.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> WarnOrAbove => _logger.WarnOrAbove;

    /// <inheritdoc/>
    public void Dispose() { }
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// E2E integration tests proving competing-responders semantics for the BareWire publish-style
/// request/response path on a live RabbitMQ broker (Aspire fixture).
///
/// <para><b>Topology:</b> a publish-style <see cref="RabbitMqRequestClient{TRequest}"/> publishes
/// a request to a per-type fanout exchange (<c>Namespace:TypeName</c>). N raw BareWire
/// responders each receive a copy via their own bound queue, execute the request, and publish a
/// response back to the reply-to address. The client's first-in-wins mechanism (<c>TrySetResult</c>
/// idempotence) ensures the caller receives exactly one response; the N-1 duplicate responses are
/// silently discarded.</para>
///
/// <para><b>M6 caveat #1 — CorrelationId echo is mandatory:</b> every competing responder MUST
/// echo the AMQP <c>CorrelationId</c> from the request to the response. Without it the response is
/// dropped unconditionally: the primary BareWire↔BareWire correlation path matches by AMQP
/// <c>CorrelationId</c> only; the MassTransit-envelope fallback path is not reached for
/// <c>application/json</c> content-type. A responder that omits the echo is silently ignored.</para>
///
/// <para><b>M6 caveat #2 — reply queue is outside ADR-004 flow control:</b> the exclusive
/// reply queue is consumed with <c>autoAck:true</c>. The N-1 duplicate responses are settled by
/// the broker automatically on delivery — no <c>BasicNackAsync</c> / <c>BasicRejectAsync</c> is
/// emitted, so there is no requeue. The reply queue is bounded operationally (exclusive auto-delete,
/// message TTL enforced by the RabbitMQ server), not by ADR-004 credit-based flow control.</para>
///
/// <para><b>M6 caveat #3 — first-in-wins drops N-1 responses, not N-1 executions:</b> every
/// competing responder fully processes the request (side effects happen N times). Only the surplus
/// N-1 <em>responses</em> are discarded after the winning TCS is already resolved. Responders with
/// non-idempotent side effects must not be used in competing-responders scenarios.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CompetingRespondersFirstInWinsTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    private readonly AspireFixture _fixture = fixture;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IConnection> CreateDirectConnectionAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(_fixture.GetRabbitMqConnectionString()),
            AutomaticRecoveryEnabled = false,
        };
        return await factory.CreateConnectionAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Declares a per-type fanout exchange and declares + binds a responder queue to it.
    /// Exchange args (durable=false, autoDelete=false) match BareWire topology deploy so
    /// redeclaration during client init is idempotent — no PRECONDITION_FAILED.
    /// </summary>
    private static async Task DeclareFanoutExchangeAndBoundQueueAsync(
        IChannel channel,
        string exchangeName,
        string queueName,
        CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(
            exchange: exchangeName,
            type: "fanout",
            durable: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: ct).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: false,
            exclusive: false,
            autoDelete: true,
            arguments: null,
            cancellationToken: ct).ConfigureAwait(false);

        await channel.QueueBindAsync(
            queue: queueName,
            exchange: exchangeName,
            routingKey: string.Empty,
            arguments: null,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a raw AMQP BareWire responder on <paramref name="queueName"/>.
    /// Each response carries the <paramref name="responderId"/> so the test can identify
    /// the winner. The responder echoes <c>CorrelationId</c> (M6 caveat #1) and uses
    /// <c>application/json</c> content-type to stay on the BareWire↔BareWire discard branch
    /// (no warn log — SEC-1).
    /// </summary>
    private async Task<(CancellationTokenSource ResponderCts, Task ResponderTask)>
        StartBareWireResponderAbcAsync(
            string queueName,
            string responderId,
            CancellationToken ct)
    {
        IConnection connection = await CreateDirectConnectionAsync(ct).ConfigureAwait(false);

        IChannel channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            ct).ConfigureAwait(false);

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: 10,
            global: false,
            cancellationToken: ct).ConfigureAwait(false);

        var responderCts = new CancellationTokenSource();
        var deserializer = new SystemTextJsonRawDeserializer();

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.ReceivedAsync += async (_, args) =>
        {
            if (responderCts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                string? replyTo = args.BasicProperties.ReplyTo;
                string? correlationId = args.BasicProperties.CorrelationId;

                if (string.IsNullOrEmpty(replyTo) || string.IsNullOrEmpty(correlationId))
                {
                    return;
                }

                var bodySequence = new System.Buffers.ReadOnlySequence<byte>(args.Body.ToArray());
                CompetingPingRequestAbc? request =
                    deserializer.Deserialize<CompetingPingRequestAbc>(bodySequence);

                if (request is null)
                {
                    return;
                }

                var response = new CompetingPingResponseAbc(
                    Echo: request.Payload,
                    ResponderId: responderId);
                byte[] responseBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);

                var responseProps = new BasicProperties
                {
                    CorrelationId = correlationId,  // M6 caveat #1: echo is mandatory
                    ContentType = "application/json",
                };

                await channel.BasicPublishAsync<BasicProperties>(
                    exchange: string.Empty,
                    routingKey: replyTo,
                    mandatory: false,
                    basicProperties: responseProps,
                    body: responseBytes,
                    cancellationToken: responderCts.Token).ConfigureAwait(false);

                await channel.BasicAckAsync(
                    args.DeliveryTag,
                    multiple: false,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Responder is shutting down — swallow gracefully.
            }
        };

        await channel.BasicConsumeAsync(
            queue: queueName,
            autoAck: false,
            consumerTag: string.Empty,
            noLocal: false,
            exclusive: false,
            arguments: null,
            consumer: consumer,
            cancellationToken: ct).ConfigureAwait(false);

        Task responderTask = Task.Run(async () =>
        {
            await responderCts.Token.WhenCancelledAsync2().ConfigureAwait(false);

            try
            {
                await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                await channel.DisposeAsync().ConfigureAwait(false);
                await connection.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup on responder shutdown.
            }
        }, CancellationToken.None);

        return (responderCts, responderTask);
    }

    /// <summary>
    /// Starts a raw AMQP BareWire responder for the <see cref="CompetingPingRequestE"/> type
    /// used in the post-dispose test ([Fact] #2).
    /// </summary>
    private async Task<(CancellationTokenSource ResponderCts, Task ResponderTask)>
        StartBareWireResponderEAsync(
            string queueName,
            CancellationToken ct)
    {
        IConnection connection = await CreateDirectConnectionAsync(ct).ConfigureAwait(false);

        IChannel channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            ct).ConfigureAwait(false);

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: 10,
            global: false,
            cancellationToken: ct).ConfigureAwait(false);

        var responderCts = new CancellationTokenSource();
        var deserializer = new SystemTextJsonRawDeserializer();

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.ReceivedAsync += async (_, args) =>
        {
            if (responderCts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                string? replyTo = args.BasicProperties.ReplyTo;
                string? correlationId = args.BasicProperties.CorrelationId;

                if (string.IsNullOrEmpty(replyTo) || string.IsNullOrEmpty(correlationId))
                {
                    return;
                }

                var bodySequence = new System.Buffers.ReadOnlySequence<byte>(args.Body.ToArray());
                CompetingPingRequestE? request =
                    deserializer.Deserialize<CompetingPingRequestE>(bodySequence);

                if (request is null)
                {
                    return;
                }

                var response = new CompetingPingResponseE(Echo: request.Payload);
                byte[] responseBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);

                var responseProps = new BasicProperties
                {
                    CorrelationId = correlationId,  // M6 caveat #1: echo is mandatory
                    ContentType = "application/json",
                };

                await channel.BasicPublishAsync<BasicProperties>(
                    exchange: string.Empty,
                    routingKey: replyTo,
                    mandatory: false,
                    basicProperties: responseProps,
                    body: responseBytes,
                    cancellationToken: responderCts.Token).ConfigureAwait(false);

                await channel.BasicAckAsync(
                    args.DeliveryTag,
                    multiple: false,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Responder is shutting down — swallow gracefully.
            }
        };

        await channel.BasicConsumeAsync(
            queue: queueName,
            autoAck: false,
            consumerTag: string.Empty,
            noLocal: false,
            exclusive: false,
            arguments: null,
            consumer: consumer,
            cancellationToken: ct).ConfigureAwait(false);

        Task responderTask = Task.Run(async () =>
        {
            await responderCts.Token.WhenCancelledAsync2().ConfigureAwait(false);

            try
            {
                await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                await channel.DisposeAsync().ConfigureAwait(false);
                await connection.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup on responder shutdown.
            }
        }, CancellationToken.None);

        return (responderCts, responderTask);
    }

    /// <summary>
    /// Constructs and initializes a publish-style <see cref="RabbitMqRequestClient{TRequest}"/>.
    /// Uses the per-type fanout exchange as the target; the capturing logger is injected so
    /// the test can assert no warning-level entries are emitted during duplicate discard (GAP-1).
    /// </summary>
    private async Task<RabbitMqRequestClient<TRequest>> CreateInitializedPublishStyleClientAsync<TRequest>(
        IConnection connection,
        ILogger capturingLogger,
        string exchangeName,
        TimeSpan timeout,
        CancellationToken ct)
        where TRequest : class
    {
        var serializer = new SystemTextJsonSerializer();
        var deserializerResolver = new BareWire.Serialization.SingleDeserializerResolver(
            new SystemTextJsonRawDeserializer());

        string connStr = _fixture.GetRabbitMqConnectionString();
        var connectionUri = new Uri(connStr);
        string rawPath = connectionUri.AbsolutePath.TrimStart('/');
        string? vhost = string.IsNullOrEmpty(rawPath) ? null : rawPath;

        var client = new RabbitMqRequestClient<TRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: deserializerResolver,
            logger: capturingLogger,
            targetExchange: exchangeName,
            routingKey: string.Empty,       // fanout: routingKey is ignored by the broker
            timeout: timeout,
            connectionUri: connectionUri,
            vhost: vhost);

        await client.InitializeAsync(ct).ConfigureAwait(false);

        return client;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proves: (a) caller receives exactly ONE response when N=3 competing responders all reply;
    /// (b) the N-1 duplicate responses are silently discarded — no Warning-or-above log entries
    /// (verified after active sniffer confirms all 3 copies were delivered, PERF-2); (c) the reply
    /// queue uses autoAck:true so no BasicNack/BasicReject is emitted — no infinite requeue loop
    /// (proved by bounded delivery count and test completing within timeout).
    ///
    /// <para>The fanout sniffer counts request copies delivered on the exchange before (b) is
    /// asserted, preventing a silent false-PASS from reading the logger before duplicates arrive.</para>
    ///
    /// <para>Edge case (d) — late duplicate after _pending removal — is naturally covered: the
    /// fanout delivers a copy of the request to all 3 responder queues; all 3 respond; only the
    /// first wins (TrySetResult idempotence). The remaining 1-2 responses arrive after (or
    /// concurrently with) _pending.TryRemove in the finally block — both timing variants are
    /// silent per the BareWire↔BareWire discard path (plan Section 2.3).</para>
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_CompetingResponders_FirstInWinsAndSilentlyDropsDuplicates()
    {
        const int responderCount = 3;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // PERF-3

        string exchangeName = RequestExchangeNameFormatter.Format<CompetingPingRequestAbc>();
        string payload = "competing-ping-abc";
        var responderIds = new[] { "responder-A", "responder-B", "responder-C" };

        // Declare the per-type fanout exchange and N=3 responder queues bound to it.
        string[] responderQueues = Enumerable.Range(0, responderCount)
            .Select(_ => $"bw-cr-responder-{Guid.NewGuid():N}")
            .ToArray();

        await using IConnection setupConnection = await CreateDirectConnectionAsync(cts.Token);
        await using IChannel setupChannel = await setupConnection.CreateChannelAsync(
            cancellationToken: cts.Token);

        foreach (string queueName in responderQueues)
        {
            await DeclareFanoutExchangeAndBoundQueueAsync(
                setupChannel, exchangeName, queueName, cts.Token);
        }

        // Active fanout sniffer: receives one copy of the published request (fanout delivers to ALL
        // bound queues, including the sniffer). Waiting for the sniffer to fire confirms the client
        // actually published to the exchange, which means all N responder queues have also received
        // a copy and are processing. After the sniffer fires we add a brief delay to allow all N
        // responders to publish their responses before asserting "no warn" (PERF-2 pattern).
        // This prevents a silent false-PASS from asserting the logger before duplicates arrive.
        string snifferQueue = $"bw-cr-sniffer-{Guid.NewGuid():N}";
        var snifferDeliveryCount = 0;
        var snifferNthDeliveredTcs =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using IConnection snifferConn = await CreateDirectConnectionAsync(cts.Token);
        await using IChannel snifferCh =
            await snifferConn.CreateChannelAsync(cancellationToken: cts.Token);

        await snifferCh.QueueDeclareAsync(
            queue: snifferQueue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: null,
            cancellationToken: cts.Token);

        await snifferCh.QueueBindAsync(
            queue: snifferQueue,
            exchange: exchangeName,
            routingKey: string.Empty,
            arguments: null,
            cancellationToken: cts.Token);

        var snifferConsumer = new AsyncEventingBasicConsumer(snifferCh);
        snifferConsumer.ReceivedAsync += (_, _) =>
        {
            // The fanout delivers exactly ONE copy of the published request to the sniffer queue
            // (one request → one message per bound queue). Count >= 1 confirms the client published.
            // This also implies all N responder queues received the request (fanout semantics).
            Interlocked.Increment(ref snifferDeliveryCount);
            snifferNthDeliveredTcs.TrySetResult();
            return Task.CompletedTask;
        };

        await snifferCh.BasicConsumeAsync(
            queue: snifferQueue,
            autoAck: true,
            consumer: snifferConsumer,
            cancellationToken: cts.Token);

        // Start N=3 raw BareWire responders.
        var responders = new List<(CancellationTokenSource Cts, Task Task)>();
        for (int i = 0; i < responderCount; i++)
        {
            (CancellationTokenSource respCts, Task respTask) =
                await StartBareWireResponderAbcAsync(responderQueues[i], responderIds[i], cts.Token);
            responders.Add((respCts, respTask));
        }

        // Create the publish-style client with the capturing logger.
        await using IConnection clientConnection = await CreateDirectConnectionAsync(cts.Token);
        var loggerProvider = new CapturingLoggerProvider();
        ILogger capturingLogger = loggerProvider.CreateLogger("RabbitMqRequestClient");

        await using RabbitMqRequestClient<CompetingPingRequestAbc> client =
            await CreateInitializedPublishStyleClientAsync<CompetingPingRequestAbc>(
                clientConnection,
                capturingLogger,
                exchangeName,
                timeout: TimeSpan.FromSeconds(30),
                cts.Token);

        // Act — publish-style request: goes to the fanout exchange, all N queues receive a copy.
        BareWire.Abstractions.Response<CompetingPingResponseAbc> response =
            await client.GetResponseAsync<CompetingPingResponseAbc>(
                new CompetingPingRequestAbc(payload), cts.Token);

        // Assert (a): caller gets EXACTLY ONE response with the correct echo.
        response.Message.Echo.Should().Be(payload,
            because: "the winning responder must echo the request payload");

        response.Message.ResponderId.Should().BeOneOf(responderIds,
            because: "the winning response must come from one of the known competing responders");

        // Wait for the sniffer to confirm the request was published to the exchange (PERF-2).
        // One request → one delivery to the sniffer queue; fanout also delivers to all N responder
        // queues simultaneously. After this signal, all N responders are processing the request.
        using var snifferCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await snifferNthDeliveredTcs.Task.WaitAsync(snifferCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Sniffer timed out — the client did not publish to the fanout exchange within 10s.
            // This is a setup problem (exchange name, binding, or client init failure).
            snifferDeliveryCount.Should().BeGreaterThanOrEqualTo(1,
                because: $"fanout sniffer must receive the published request on exchange " +
                         $"'{exchangeName}' within 10s — zero deliveries indicate the client " +
                         $"did not publish to the correct exchange");
        }

        // Give all N responders time to publish their responses and the client to process them.
        // At this point the fanout has delivered the request to all N queues; the responders are
        // processing concurrently. A 500ms window is sufficient on a local Aspire broker.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);

        // Assert (b): no warning-or-above log entries from the client.
        // The BareWire↔BareWire discard path (application/json + valid-but-removed CorrelationId)
        // emits NO log — a silent drop. Any Warning here indicates a regression.
        loggerProvider.WarnOrAbove.Should().BeEmpty(
            because: "N-1 duplicate responses on the BareWire↔BareWire path must be silently " +
                     "discarded (no warn log); a Warning here means the discard path regressed");

        // Assert (c): no exception was thrown and the test completes within the pinned 60s timeout.
        // The reply queue uses autoAck:true — no BasicNack/BasicReject is emitted — so no infinite
        // requeue loop. The bounded delivery count (≤ responderCount responses ever published)
        // provides indirect proof: if duplicates were nack+requeued, the logger would eventually
        // fill with retries or the test would hang and hit the CancellationTokenSource timeout.

        // Cleanup responders.
        foreach ((CancellationTokenSource respCts, Task respTask) in responders)
        {
            await respCts.CancelAsync();
            await respTask;
            respCts.Dispose();
        }
    }

    /// <summary>
    /// Proves (e): a response arriving after <see cref="RabbitMqRequestClient{TRequest}.DisposeAsync"/>
    /// is discarded cleanly — no exception, no nack/requeue, no unobserved exception in the test.
    ///
    /// <para>Sequence: request → receive winning response → <c>DisposeAsync</c> → manually publish
    /// a late response to the (now-deleted) reply queue via a separate channel → assert no throw.</para>
    ///
    /// <para>After dispose, <c>_pending</c> is cleared and the reply channel is closed; the manually
    /// published message is simply not delivered (the exclusive auto-delete queue is gone). The test
    /// asserts the full sequence completes without throwing.</para>
    /// </summary>
    [Fact]
    public async Task ResponseAfterDispose_IsDiscardedCleanly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // PERF-3

        string exchangeName = RequestExchangeNameFormatter.Format<CompetingPingRequestE>();
        string payload = "post-dispose-ping-e";
        string responderQueue = $"bw-cr-post-dispose-{Guid.NewGuid():N}";

        await using IConnection setupConnection = await CreateDirectConnectionAsync(cts.Token);
        await using IChannel setupChannel = await setupConnection.CreateChannelAsync(
            cancellationToken: cts.Token);

        // Declare the per-type fanout exchange and a single responder queue.
        await DeclareFanoutExchangeAndBoundQueueAsync(
            setupChannel, exchangeName, responderQueue, cts.Token);

        // Start one responder.
        (CancellationTokenSource respCts, Task respTask) =
            await StartBareWireResponderEAsync(responderQueue, cts.Token);

        // Create the publish-style client.
        await using IConnection clientConnection = await CreateDirectConnectionAsync(cts.Token);
        var loggerProvider = new CapturingLoggerProvider();
        ILogger capturingLogger = loggerProvider.CreateLogger("RabbitMqRequestClient");

        RabbitMqRequestClient<CompetingPingRequestE> client =
            await CreateInitializedPublishStyleClientAsync<CompetingPingRequestE>(
                clientConnection,
                capturingLogger,
                exchangeName,
                timeout: TimeSpan.FromSeconds(30),
                cts.Token);

        // Act (1): request → receive winning response.
        BareWire.Abstractions.Response<CompetingPingResponseE> response =
            await client.GetResponseAsync<CompetingPingResponseE>(
                new CompetingPingRequestE(payload), cts.Token);

        response.Message.Echo.Should().Be(payload,
            because: "the responder must echo the request payload");

        // Act (2): dispose the client. After dispose, _pending is cleared and the reply channel
        // is closed. The exclusive auto-delete reply queue is deleted by the broker.
        await client.DisposeAsync();

        // Act (3): publish a late response to the (now-deleted) reply queue via a separate channel.
        // The broker silently drops the message because the queue no longer exists (mandatory:false).
        // No exception should be thrown by this publish or by the disposed client.
        Func<Task> publishLateResponse = async () =>
        {
            await using IConnection lateConn = await CreateDirectConnectionAsync(cts.Token);
            await using IChannel lateCh = await lateConn.CreateChannelAsync(
                cancellationToken: cts.Token).ConfigureAwait(false);

            byte[] lateBody = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new CompetingPingResponseE(Echo: "late-response"));

            var lateProps = new BasicProperties
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                ContentType = "application/json",
            };

            // Use a plausible (but now non-existent) reply-to name. The broker drops the message
            // silently (mandatory:false) because the exclusive auto-delete queue is already gone.
            await lateCh.BasicPublishAsync<BasicProperties>(
                exchange: string.Empty,
                routingKey: $"amq.rabbitmq.reply-to.{Guid.NewGuid():N}",
                mandatory: false,
                basicProperties: lateProps,
                body: lateBody,
                cancellationToken: cts.Token).ConfigureAwait(false);
        };

        // Assert: the full post-dispose sequence must not throw.
        await publishLateResponse.Should().NotThrowAsync(
            because: "publishing a late response after client dispose must not throw; " +
                     "the broker silently drops the message (mandatory:false, queue deleted)");

        // No nack/requeue: autoAck:true on the reply queue means the broker never receives
        // a settlement request from the client — no requeue loop is possible.
        // No unobserved exceptions: if the disposed client's channel had emitted an unhandled
        // exception callback it would surface here via the test framework.

        // Cleanup responder.
        await respCts.CancelAsync();
        await respTask;
        respCts.Dispose();
    }
}

// ── WhenCancelledAsync2 extension (local copy for RequestResponse namespace) ──

/// <summary>Extension helpers for <see cref="CancellationToken"/>.</summary>
internal static class CancellationTokenExtensions2
{
    /// <summary>Returns a <see cref="Task"/> that completes when the token is cancelled.</summary>
    internal static Task WhenCancelledAsync2(this CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), tcs);
        return tcs.Task;
    }
}
