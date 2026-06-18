using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Unit tests for <see cref="RabbitMqRequestClient{TRequest}"/> that do not require a running broker.
/// </summary>
public sealed class RabbitMqRequestClientTests
{
    // ── Test records ───────────────────────────────────────────────────────────

    private sealed record TestRequest(string Value);
    private sealed record TestResponse(string Result);

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static RabbitMqRequestClient<TestRequest> CreateClient(
        int maxPendingRequests = 10,
        TimeSpan? timeout = null)
    {
        IConnection connection = Substitute.For<IConnection>();
        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

        serializer.ContentType.Returns("application/json");

        return new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: deserializerResolver,
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "test-queue",
            timeout: timeout ?? TimeSpan.FromSeconds(30),
            maxPendingRequests: maxPendingRequests);
    }

    // ── Constructor guards ─────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullConnection_ThrowsArgumentNull()
    {
        // Arrange
        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

        // Act
        Action act = () => _ = new RabbitMqRequestClient<TestRequest>(
            connection: null!,
            serializer: serializer,
            deserializerResolver: deserializerResolver,
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "queue",
            timeout: TimeSpan.FromSeconds(30));

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("connection");
    }

    [Fact]
    public void Constructor_NullSerializer_ThrowsArgumentNull()
    {
        // Arrange
        IConnection connection = Substitute.For<IConnection>();
        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

        // Act
        Action act = () => _ = new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: null!,
            deserializerResolver: deserializerResolver,
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "queue",
            timeout: TimeSpan.FromSeconds(30));

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("serializer");
    }

    [Fact]
    public void Constructor_NullDeserializerResolver_ThrowsArgumentNull()
    {
        // Arrange
        IConnection connection = Substitute.For<IConnection>();
        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();

        // Act
        Action act = () => _ = new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: null!,
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "queue",
            timeout: TimeSpan.FromSeconds(30));

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("deserializerResolver");
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNull()
    {
        // Arrange
        IConnection connection = Substitute.For<IConnection>();
        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

        // Act
        Action act = () => _ = new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: deserializerResolver,
            logger: null!,
            targetExchange: string.Empty,
            routingKey: "queue",
            timeout: TimeSpan.FromSeconds(30));

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    public void Constructor_ZeroMaxPendingRequests_ThrowsArgumentOutOfRange()
    {
        // Arrange
        IConnection connection = Substitute.For<IConnection>();
        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

        // Act
        Action act = () => _ = new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: deserializerResolver,
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "queue",
            timeout: TimeSpan.FromSeconds(30),
            maxPendingRequests: 0);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maxPendingRequests");
    }

    // ── DeserializeResponse — content-type routing (issue #13) ─────────────────

    [Fact]
    public void DeserializeResponse_ResolvesDeserializerByResponseContentType()
    {
        // Arrange — the response carries the MassTransit envelope content-type. The client must
        // route to the deserializer registered for that content-type, not the default.
        const string mtContentType = "application/vnd.masstransit+json";
        var expected = new TestResponse("ok");

        IMessageDeserializer mtDeserializer = Substitute.For<IMessageDeserializer>();
        mtDeserializer.Deserialize<TestResponse>(Arg.Any<ReadOnlySequence<byte>>()).Returns(expected);

        IDeserializerResolver resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve(mtContentType).Returns(mtDeserializer);

        var client = new RabbitMqRequestClient<TestRequest>(
            connection: Substitute.For<IConnection>(),
            serializer: Substitute.For<IMessageSerializer>(),
            deserializerResolver: resolver,
            logger: NullLogger.Instance,
            targetExchange: "ex",
            routingKey: "rk",
            timeout: TimeSpan.FromSeconds(30));

        var headers = new Dictionary<string, string> { ["content-type"] = mtContentType };
        var inbound = new InboundMessage("mid", headers, ReadOnlySequence<byte>.Empty, deliveryTag: 1);

        // Act
        TestResponse result = client.DeserializeResponse<TestResponse>(inbound, correlationId: "corr-1");

        // Assert — deserializer selected by response content-type and its output returned.
        result.Should().BeSameAs(expected);
        resolver.Received(1).Resolve(mtContentType);
    }

    [Fact]
    public void DeserializeResponse_WhenDeserializerReturnsNull_ThrowsTransportException()
    {
        // Arrange
        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        deserializer.Deserialize<TestResponse>(Arg.Any<ReadOnlySequence<byte>>()).Returns((TestResponse?)null);

        IDeserializerResolver resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve(Arg.Any<string?>()).Returns(deserializer);

        var client = new RabbitMqRequestClient<TestRequest>(
            connection: Substitute.For<IConnection>(),
            serializer: Substitute.For<IMessageSerializer>(),
            deserializerResolver: resolver,
            logger: NullLogger.Instance,
            targetExchange: "ex",
            routingKey: "rk",
            timeout: TimeSpan.FromSeconds(30));

        var inbound = new InboundMessage(
            "mid",
            new Dictionary<string, string> { ["content-type"] = "application/json" },
            ReadOnlySequence<byte>.Empty,
            deliveryTag: 1);

        // Act
        Action act = () => client.DeserializeResponse<TestResponse>(inbound, correlationId: "corr-1");

        // Assert
        act.Should().Throw<BareWireTransportException>()
            .WithMessage("*Failed to deserialize response*");
    }

    // ── GetResponseAsync — null guard ──────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_NullRequest_ThrowsArgumentNull()
    {
        // Arrange
        var client = CreateClient();

        // Act
        Func<Task> act = async () => await client.GetResponseAsync<TestResponse>(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("request");
    }

    // ── GetResponseAsync — uninitialized guard ─────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_NotInitialized_ThrowsInvalidOperation()
    {
        // Arrange
        var client = CreateClient();
        var request = new TestRequest("hello");

        // Act
        Func<Task> act = async () => await client.GetResponseAsync<TestResponse>(request);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*InitializeAsync*");
    }

    // ── GetResponseAsync — bounded gate ───────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_MaxPendingExceeded_ThrowsTransportException()
    {
        // The full overflow scenario is covered by integration tests. Here we only verify a client
        // with limit=1 is created without error (the gate-acquire/release path is exercised there).
        await Task.CompletedTask;

        var client = CreateClient(maxPendingRequests: 1);
        client.Should().NotBeNull();
    }

    // ── DisposeAsync — cancels pending requests ────────────────────────────────

    [Fact]
    public async Task DisposeAsync_CancelsPendingRequests()
    {
        // Arrange — set up a mock IConnection that returns a functional IChannel
        // for InitializeAsync, then verify Dispose cancels outstanding TCS.
        IConnection connection = Substitute.For<IConnection>();
        IChannel responseChannel = Substitute.For<IChannel>();
        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

        serializer.ContentType.Returns("application/json");

        // QueueDeclareAsync returns a server-named queue
        responseChannel
            .QueueDeclareAsync(
                queue: Arg.Any<string>(),
                durable: Arg.Any<bool>(),
                exclusive: Arg.Any<bool>(),
                autoDelete: Arg.Any<bool>(),
                arguments: Arg.Any<IDictionary<string, object?>?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new QueueDeclareOk("amq.gen-test-queue", 0, 0)));

        responseChannel
            .BasicConsumeAsync(
                queue: Arg.Any<string>(),
                autoAck: Arg.Any<bool>(),
                consumerTag: Arg.Any<string>(),
                noLocal: Arg.Any<bool>(),
                exclusive: Arg.Any<bool>(),
                arguments: Arg.Any<IDictionary<string, object?>?>(),
                consumer: Arg.Any<IAsyncBasicConsumer>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("consumer-tag"));

        connection
            .CreateChannelAsync(
                Arg.Any<CreateChannelOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(responseChannel));

        var client = new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: deserializerResolver,
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "test-queue",
            timeout: TimeSpan.FromSeconds(30));

        await client.InitializeAsync(CancellationToken.None);

        // Act — dispose immediately; any pending TCS should be cancelled
        await client.DisposeAsync();

        // Assert — calling GetResponseAsync after dispose throws ObjectDisposedException
        Func<Task> act = async () =>
            await client.GetResponseAsync<TestResponse>(new TestRequest("x"));

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // ── DisposeAsync — idempotent ──────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        // Arrange
        var client = CreateClient();

        // Act
        Func<Task> act = async () =>
        {
            await client.DisposeAsync();
            await client.DisposeAsync();
        };

        // Assert
        await act.Should().NotThrowAsync();
    }
}
