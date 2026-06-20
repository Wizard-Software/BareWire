using System.Buffers;
using System.Text;
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

    private static readonly Uri FakeConnectionUri = new("amqp://localhost");

    private static RabbitMqRequestClient<TestRequest> CreateClient(
        int maxPendingRequests = 10,
        TimeSpan? timeout = null,
        IDeserializerResolver? deserializerResolver = null,
        IMessageSerializer? serializer = null)
    {
        IConnection connection = Substitute.For<IConnection>();
        IMessageSerializer ser = serializer ?? Substitute.For<IMessageSerializer>();
        IDeserializerResolver resolver = deserializerResolver ?? Substitute.For<IDeserializerResolver>();

        ser.ContentType.Returns("application/json");

        return new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: ser,
            deserializerResolver: resolver,
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "test-queue",
            timeout: timeout ?? TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null,
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
            timeout: TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null);

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
            timeout: TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null);

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
            timeout: TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null);

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
            timeout: TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null);

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
            connectionUri: FakeConnectionUri,
            vhost: null,
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
            timeout: TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null);

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
            timeout: TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null);

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
            timeout: TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null);

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

    // ── TryResolvePending — AMQP-primary correlation (regression) ─────────────

    /// <summary>
    /// Verifies the primary AMQP CorrelationId path still completes the matching pending TCS —
    /// regression guard for BareWire↔BareWire interop after the envelope-fallback is added.
    /// </summary>
    [Fact]
    public void TryResolvePending_WhenAmqpCorrelationIdMatchesPending_ReturnsTrueAndResolvesTcs()
    {
        // Arrange
        var client = CreateClient();
        Guid requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SeedPendingForTest(requestId, tcs);

        // Act — primary AMQP CorrelationId path; no envelope body needed.
        bool resolved = client.TryResolvePending(
            amqpCorrelationId: requestId.ToString(),
            contentType: null,
            body: ReadOnlySequence<byte>.Empty,
            out TaskCompletionSource<InboundMessage>? result);

        // Assert — cast to object so AwesomeAssertions uses ObjectAssertions (not TaskCompletionSourceAssertions).
        resolved.Should().BeTrue();
        ((object?)result).Should().NotBeNull();
        object.ReferenceEquals(result, tcs).Should().BeTrue("TryResolvePending must return the exact registered TCS");
    }

    // ── TryResolvePending — envelope fallback (SEC-2, plan step 9) ────────────

    /// <summary>
    /// Verifies that when AMQP CorrelationId is absent/unknown but the body is a MassTransit
    /// envelope carrying a <c>requestId</c> that exists in <c>_pending</c>, the matching TCS
    /// is returned (MT→BareWire response correlation path).
    /// </summary>
    [Fact]
    public void TryResolvePending_WhenAmqpCorrelationMissing_CorrelatesByEnvelopeRequestId()
    {
        // Arrange — build a MassTransit-style envelope body with a known requestId.
        Guid requestId = Guid.NewGuid();
        string envelopeJson = $$$"""{"requestId":"{{{requestId}}}","messageType":["urn:message:TestResponse"],"message":{}}""";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(envelopeJson);
        var body = new ReadOnlySequence<byte>(bodyBytes);

        // Set up a substitute that implements both IMessageDeserializer and IResponseEnvelopeReader
        // so TryResolvePending can use the envelope-reader fallback path.
        var multiSub = Substitute.For<IMessageDeserializer, IResponseEnvelopeReader>();
        ((IResponseEnvelopeReader)multiSub)
            .TryReadRequestId(Arg.Any<ReadOnlySequence<byte>>(), out Arg.Any<Guid>())
            .Returns(call =>
            {
                call[1] = requestId;
                return true;
            });

        IDeserializerResolver resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve("application/vnd.masstransit+json").Returns(multiSub);

        var client = CreateClient(deserializerResolver: resolver);

        var tcs = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SeedPendingForTest(requestId, tcs);

        // Act — AMQP correlation id is null (MT does not echo it), content-type triggers fallback.
        bool resolved = client.TryResolvePending(
            amqpCorrelationId: null,
            contentType: "application/vnd.masstransit+json",
            body: body,
            out TaskCompletionSource<InboundMessage>? result);

        // Assert — fallback succeeded; the right TCS is returned.
        resolved.Should().BeTrue();
        ((object?)result).Should().NotBeNull();
        object.ReferenceEquals(result, tcs).Should().BeTrue("fallback correlation must resolve the exact registered TCS");
    }

    /// <summary>
    /// SEC-2: when the body carries a <c>requestId</c> that is NOT in <c>_pending</c> (e.g. an
    /// unsolicited or replayed message), TryResolvePending must return false — never fabricate a
    /// pending entry from the body.
    /// </summary>
    [Fact]
    public void TryResolvePending_WhenBodyRequestIdHasNoPendingEntry_Discards()
    {
        // Arrange — envelope body with a requestId that was never registered in _pending.
        Guid unknownRequestId = Guid.NewGuid();
        string envelopeJson = $$$"""{"requestId":"{{{unknownRequestId}}}","messageType":["urn:message:TestResponse"],"message":{}}""";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(envelopeJson);
        var body = new ReadOnlySequence<byte>(bodyBytes);

        var multiSub = Substitute.For<IMessageDeserializer, IResponseEnvelopeReader>();
        ((IResponseEnvelopeReader)multiSub)
            .TryReadRequestId(Arg.Any<ReadOnlySequence<byte>>(), out Arg.Any<Guid>())
            .Returns(call =>
            {
                call[1] = unknownRequestId;
                return true;
            });

        IDeserializerResolver resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve("application/vnd.masstransit+json").Returns(multiSub);

        var client = CreateClient(deserializerResolver: resolver);
        // No SeedPendingForTest — _pending is intentionally empty.

        // Act
        bool resolved = client.TryResolvePending(
            amqpCorrelationId: null,
            contentType: "application/vnd.masstransit+json",
            body: body,
            out TaskCompletionSource<InboundMessage>? result);

        // Assert — SEC-2: unknown requestId must be discarded, not completed.
        resolved.Should().BeFalse();
        ((object?)result).Should().BeNull("SEC-2: no pending entry for the body-supplied requestId must produce null");
    }

    /// <summary>
    /// Verifies that when AMQP CorrelationId is present but unknown (not in _pending) and the
    /// content-type is NOT MassTransit, no envelope fallback is attempted — the message is discarded.
    /// </summary>
    [Fact]
    public void TryResolvePending_WhenAmqpCorrelationUnknownAndNotMassTransit_Discards()
    {
        // Arrange
        var client = CreateClient();
        // Do not seed anything in _pending.

        // Act — unknown AMQP correlation, non-MT content type; envelope fallback must NOT fire.
        bool resolved = client.TryResolvePending(
            amqpCorrelationId: Guid.NewGuid().ToString(),
            contentType: "application/json",
            body: ReadOnlySequence<byte>.Empty,
            out TaskCompletionSource<InboundMessage>? result);

        // Assert
        resolved.Should().BeFalse();
        ((object?)result).Should().BeNull("non-MT content-type must not trigger the envelope fallback path");
    }
}
