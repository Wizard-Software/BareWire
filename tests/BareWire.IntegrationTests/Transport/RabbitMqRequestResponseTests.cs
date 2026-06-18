using System.Collections.Concurrent;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Serialization;
using BareWire.Serialization.Json;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Integration tests for the request-response pattern via <see cref="RabbitMqRequestClient{TRequest}"/>.
/// All tests require a running RabbitMQ instance provisioned via <see cref="AspireFixture"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMqRequestResponseTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    private readonly AspireFixture _fixture = fixture;

    // ── Test records ───────────────────────────────────────────────────────────

    private sealed record PingRequest(string Payload);
    private sealed record PingResponse(string Echo);

    // ── Helpers ────────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter()
    {
        var options = new RabbitMqTransportOptions
        {
            ConnectionString = _fixture.GetRabbitMqConnectionString(),
        };
        return new RabbitMqTransportAdapter(options, NullLogger<RabbitMqTransportAdapter>.Instance);
    }

    private async Task<IConnection> CreateDirectConnectionAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(_fixture.GetRabbitMqConnectionString()),
            AutomaticRecoveryEnabled = false,
        };
        return await factory.CreateConnectionAsync(ct).ConfigureAwait(false);
    }

    private static async Task DeployQueueAsync(
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
    {
        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        await adapter.DeployTopologyAsync(configurator.Build(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a <see cref="RabbitMqRequestClient{TRequest}"/> targeting the default exchange
    /// with routing key = <paramref name="queueName"/>, initializes it, and returns it.
    /// The caller owns disposal.
    /// </summary>
    private static async Task<RabbitMqRequestClient<PingRequest>> CreateInitializedRequestClientAsync(
        IConnection connection,
        TimeSpan timeout,
        string queueName,
        CancellationToken ct)
    {
        var serializer = new SystemTextJsonSerializer();
        var deserializerResolver = new SingleDeserializerResolver(new SystemTextJsonRawDeserializer());

        var client = new RabbitMqRequestClient<PingRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: deserializerResolver,
            logger: NullLogger.Instance,
            targetExchange: string.Empty,   // default exchange routes by routing key = queue name
            routingKey: queueName,
            timeout: timeout);

        await client.InitializeAsync(ct).ConfigureAwait(false);

        return client;
    }

    /// <summary>
    /// Starts a background responder that consumes messages from <paramref name="queueName"/>,
    /// deserializes each as <see cref="PingRequest"/>, and publishes a <see cref="PingResponse"/>
    /// with Echo = request.Payload back to the reply queue indicated by the AMQP <c>ReplyTo</c>
    /// property, using the same <c>CorrelationId</c>.
    /// </summary>
    /// <returns>
    /// A <see cref="CancellationTokenSource"/> that stops the responder when cancelled,
    /// and the background <see cref="Task"/>. Call <c>cts.Cancel()</c> to stop, then await
    /// the task to confirm the responder exited cleanly.
    /// </returns>
    private async Task<(CancellationTokenSource ResponderCts, Task ResponderTask)> StartResponderAsync(
        string queueName,
        CancellationToken ct)
    {
        IConnection responderConnection = await CreateDirectConnectionAsync(ct).ConfigureAwait(false);

        IChannel responderChannel = await responderConnection
            .CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                ct)
            .ConfigureAwait(false);

        await responderChannel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: 10,
            global: false,
            cancellationToken: ct)
            .ConfigureAwait(false);

        var responderCts = new CancellationTokenSource();

        var deserializer = new SystemTextJsonRawDeserializer();
        var serializer = new SystemTextJsonSerializer();

        var consumer = new AsyncEventingBasicConsumer(responderChannel);

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
                PingRequest? request = deserializer.Deserialize<PingRequest>(bodySequence);

                if (request is null)
                {
                    return;
                }

                var response = new PingResponse(Echo: request.Payload);

                // Serialize response into a pooled writer then copy to array for publish.
                byte[] responseBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);

                var responseProps = new BasicProperties
                {
                    CorrelationId = correlationId,
                    ContentType = "application/json",
                };

                await responderChannel.BasicPublishAsync<BasicProperties>(
                    exchange: string.Empty,     // default exchange — routes by routing key = queue name
                    routingKey: replyTo,        // ReplyTo is the exclusive response queue name
                    mandatory: false,
                    basicProperties: responseProps,
                    body: responseBytes,
                    cancellationToken: responderCts.Token)
                    .ConfigureAwait(false);

                await responderChannel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Responder is shutting down — swallow gracefully.
            }
        };

        await responderChannel.BasicConsumeAsync(
            queue: queueName,
            autoAck: false,
            consumerTag: string.Empty,
            noLocal: false,
            exclusive: false,
            arguments: null,
            consumer: consumer,
            cancellationToken: ct)
            .ConfigureAwait(false);

        Task responderTask = Task.Run(async () =>
        {

            await responderCts.Token.WhenCancelledAsync().ConfigureAwait(false);


            try
            {
                await responderChannel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                await responderChannel.DisposeAsync().ConfigureAwait(false);
                await responderConnection.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                await responderConnection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup on responder shutdown.
            }
        }, CancellationToken.None);

        return (responderCts, responderTask);
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_HappyPath_ReturnsTypedResponse()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();
        await using IConnection requestConnection = await CreateDirectConnectionAsync(cts.Token);

        string queueName = $"rr-happy-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        (CancellationTokenSource responderCts, Task responderTask) =
            await StartResponderAsync(queueName, cts.Token);

        await using RabbitMqRequestClient<PingRequest> client =
            await CreateInitializedRequestClientAsync(requestConnection, TimeSpan.FromSeconds(10), queueName, cts.Token);

        var request = new PingRequest(Payload: "hello");

        // Act
        Response<PingResponse> response = await client.GetResponseAsync<PingResponse>(request, cts.Token);

        // Assert
        response.Message.Echo.Should().Be("hello");

        // Cleanup
        await responderCts.CancelAsync();
        await responderTask;
        responderCts.Dispose();
    }

    [Fact]
    public async Task GetResponseAsync_Timeout_ThrowsRequestTimeoutException()
    {
        // Arrange — deploy queue with no responder; short timeout so test stays fast
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();
        await using IConnection requestConnection = await CreateDirectConnectionAsync(cts.Token);

        string queueName = $"rr-timeout-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        var shortTimeout = TimeSpan.FromMilliseconds(500);
        await using RabbitMqRequestClient<PingRequest> client =
            await CreateInitializedRequestClientAsync(requestConnection, shortTimeout, queueName, cts.Token);

        var request = new PingRequest(Payload: "nobody home");

        // Act + Assert
        Func<Task> act = async () =>
            await client.GetResponseAsync<PingResponse>(request, cts.Token);

        var exception = await act.Should().ThrowAsync<RequestTimeoutException>();
        exception.Which.Timeout.Should().Be(shortTimeout);
        exception.Which.RequestType.Should().Be<PingRequest>();
    }

    [Fact]
    public async Task GetResponseAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        // Arrange — deploy queue with no responder; cancel immediately
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();
        await using IConnection requestConnection = await CreateDirectConnectionAsync(cts.Token);

        string queueName = $"rr-cancel-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        // Long timeout so the test is NOT driven by the client timeout — only by cancellation.
        await using RabbitMqRequestClient<PingRequest> client =
            await CreateInitializedRequestClientAsync(requestConnection, TimeSpan.FromSeconds(60), queueName, cts.Token);

        var request = new PingRequest(Payload: "cancel me");

        using CancellationTokenSource cancelCts = new();
        await cancelCts.CancelAsync();   // cancel before calling GetResponseAsync

        // Act + Assert — must throw OperationCanceledException, NOT RequestTimeoutException
        Func<Task> act = async () =>
            await client.GetResponseAsync<PingResponse>(request, cancelCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetResponseAsync_MultipleRequests_RoutedByCorrelationId()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();
        await using IConnection requestConnection = await CreateDirectConnectionAsync(cts.Token);

        string queueName = $"rr-multi-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        (CancellationTokenSource responderCts, Task responderTask) =
            await StartResponderAsync(queueName, cts.Token);

        await using RabbitMqRequestClient<PingRequest> client =
            await CreateInitializedRequestClientAsync(requestConnection, TimeSpan.FromSeconds(10), queueName, cts.Token);

        // Act — fire 3 concurrent requests with distinct payloads
        string[] payloads = ["alpha", "beta", "gamma"];

        Task<Response<PingResponse>>[] tasks = payloads
            .Select(p => client.GetResponseAsync<PingResponse>(new PingRequest(Payload: p), cts.Token))
            .ToArray();

        Response<PingResponse>[] responses = await Task.WhenAll(tasks);

        // Assert — each response echoes its own request payload
        var echoes = new ConcurrentBag<string>(responses.Select(r => r.Message.Echo));
        echoes.Should().BeEquivalentTo(payloads,
            because: "each concurrent request must receive its own correctly correlated response");

        // Cleanup
        await responderCts.CancelAsync();
        await responderTask;
        responderCts.Dispose();
    }

    [Fact]
    public async Task Dispose_CancelsPendingRequests()
    {
        // Arrange — deploy queue with no responder; long timeout so we control cancellation
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();
        await using IConnection requestConnection = await CreateDirectConnectionAsync(cts.Token);

        string queueName = $"rr-dispose-{Guid.NewGuid():N}";
        await DeployQueueAsync(adapter, queueName, cts.Token);

        var client = await CreateInitializedRequestClientAsync(
            requestConnection,
            timeout: TimeSpan.FromSeconds(60),   // very long — the dispose must win
            queueName,
            cts.Token);

        var request = new PingRequest(Payload: "pending");

        // Start a request that will wait indefinitely (no responder, long timeout)
        Task<Response<PingResponse>> pendingTask =
            client.GetResponseAsync<PingResponse>(request, cts.Token);

        // Give GetResponseAsync a moment to reach the await-for-response point before disposing.
        await Task.Delay(200, cts.Token);

        // Act — dispose the client while the request is in-flight
        await client.DisposeAsync();

        // Assert — the pending task must complete with OperationCanceledException
        Func<Task> act = async () => await pendingTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Regression test for issue #13: a request client created via <see cref="RabbitMqRequestClientFactory"/>
    /// must route the request through the exchange mapped for the request type
    /// (<c>MapExchange&lt;TRequest&gt;</c>), not the transport <c>DefaultExchange</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="RabbitMqTransportOptions.DefaultExchange"/> is deliberately set to a different,
    /// unbound exchange. If the request client ignored the mapping (the original bug), the request
    /// would never reach the responder and the call would time out instead of returning a response.
    /// </remarks>
    [Fact]
    public async Task GetResponseAsync_WhenExchangeMappedForRequestType_RoutesViaMappedExchange()
    {
        // Arrange — declare a direct exchange + queue bound by routing key; start a responder.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        string exchangeName = $"rr-mapped-ex-{Guid.NewGuid():N}";
        string queueName = $"rr-mapped-q-{Guid.NewGuid():N}";
        string routingKey = $"rr-mapped-rk-{Guid.NewGuid():N}";

        await DeclareDirectExchangeBoundQueueAsync(exchangeName, queueName, routingKey, cts.Token);

        (CancellationTokenSource responderCts, Task responderTask) =
            await StartResponderAsync(queueName, cts.Token);

        var options = new RabbitMqTransportOptions
        {
            ConnectionString = _fixture.GetRabbitMqConnectionString(),
            DefaultExchange = "barewire-default-unbound",   // wrong on purpose — must NOT be used
        };

        var serializerResolver = new DefaultSerializerResolver(new SystemTextJsonSerializer());
        var deserializerResolver = new SingleDeserializerResolver(new SystemTextJsonRawDeserializer());
        var exchangeResolver = new BareWire.Transport.RabbitMQ.Internal.ExchangeResolver(
            new Dictionary<Type, string> { [typeof(PingRequest)] = exchangeName });
        var routingKeyResolver = new BareWire.Transport.RabbitMQ.Internal.RoutingKeyResolver(
            new Dictionary<Type, string> { [typeof(PingRequest)] = routingKey });

        await using var factory = new RabbitMqRequestClientFactory(
            options,
            serializerResolver,
            deserializerResolver,
            exchangeResolver,
            routingKeyResolver,
            new RabbitMqHeaderMapper(),
            NullLoggerFactory.Instance);

        await using var client =
            (RabbitMqRequestClient<PingRequest>)await factory.CreateRequestClientAsync<PingRequest>(cts.Token);

        // Act
        Response<PingResponse> response =
            await client.GetResponseAsync<PingResponse>(new PingRequest("hello"), cts.Token);

        // Assert — receiving a response proves the request reached the responder via the mapped exchange.
        response.Message.Echo.Should().Be("hello");

        // Cleanup
        await responderCts.CancelAsync();
        await responderTask;
        responderCts.Dispose();
    }

    /// <summary>
    /// Declares a non-durable direct exchange, a queue, and binds the queue to the exchange with
    /// <paramref name="routingKey"/>, using a short-lived direct connection.
    /// </summary>
    private async Task DeclareDirectExchangeBoundQueueAsync(
        string exchangeName,
        string queueName,
        string routingKey,
        CancellationToken ct)
    {
        await using IConnection connection = await CreateDirectConnectionAsync(ct).ConfigureAwait(false);
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            exchange: exchangeName,
            type: "direct",
            durable: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: ct).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: false,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: ct).ConfigureAwait(false);

        await channel.QueueBindAsync(
            queue: queueName,
            exchange: exchangeName,
            routingKey: routingKey,
            arguments: null,
            cancellationToken: ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Internal helper extensions used only in request-response integration tests.
/// </summary>
file static class CancellationTokenExtensions
{
    /// <summary>
    /// Returns a <see cref="Task"/> that completes when the token is cancelled.
    /// </summary>
    internal static Task WhenCancelledAsync(this CancellationToken token)
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
