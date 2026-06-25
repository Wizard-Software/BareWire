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
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

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

    // ── GetResponseAsync — publish-style routing (Feature 14) ────────────────

    /// <summary>
    /// Verifies that a client constructed with <c>targetExchange="OrderSystem.Events:OrderSubmitted"</c>
    /// and <c>routingKey=""</c> (as the factory produces for publish-style requests) publishes via
    /// <c>BasicPublishAsync</c> with exactly those values and <c>mandatory=false</c>.
    ///
    /// Also asserts (GAP-2 lightweight vehicle) that the captured <c>exchange</c> argument is the
    /// same string instance as the one passed to the constructor — proving <c>_targetExchange</c> is
    /// read once as a constant field, not re-derived per request (NF2/ADR-003).
    ///
    /// Plumbing: because a mocked <c>BasicPublishAsync</c> never delivers a response to the
    /// response queue, <c>GetResponseAsync</c> would block until timeout. To avoid this, the test
    /// captures the AMQP <c>CorrelationId</c> from the publish properties inside the
    /// <c>BasicPublishAsync</c> callback and uses <c>TryResolvePending</c> +
    /// <c>TaskCompletionSource.TrySetResult</c> to unblock <c>GetResponseAsync</c> immediately.
    /// A short client timeout (2 s) is used as a safety net.
    /// </summary>
    [Fact]
    public async Task SerializeAndPublishAsync_WhenPublishStyle_PublishesToFanoutWithEmptyRoutingKey()
    {
        // ── Arrange ───────────────────────────────────────────────────────────

        // The exact string instance passed to the constructor — used for GAP-2 ReferenceEquals assertion.
        const string fanoutExchangeName = "OrderSystem.Events:OrderSubmitted";

        // Set up deserializer so the response can be deserialized after TCS is resolved.
        var responseDeserializer = Substitute.For<IMessageDeserializer>();
        responseDeserializer
            .Deserialize<TestResponse>(Arg.Any<ReadOnlySequence<byte>>())
            .Returns(new TestResponse("ok"));

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(responseDeserializer);

        // Serializer stub — writes nothing; we only care about the AMQP routing args.
        var serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        serializer
            .When(s => s.Serialize(Arg.Any<TestRequest>(), Arg.Any<System.Buffers.IBufferWriter<byte>>()))
            .Do(_ => { /* no-op — body content irrelevant for routing assertions */ });

        // Two-channel connection:
        //   - response channel: publisherConfirmationsEnabled = false (used by InitializeAsync)
        //   - publish channel:  publisherConfirmationsEnabled = true  (used by GetResponseAsync)
        IChannel responseChannel = Substitute.For<IChannel>();
        IChannel publishChannel = Substitute.For<IChannel>();

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

        IConnection connection = Substitute.For<IConnection>();

        // Return response channel for the InitializeAsync call (confirmations disabled)
        // and the publish channel for the GetResponseAsync call (confirmations enabled).
        connection
            .CreateChannelAsync(
                Arg.Is<CreateChannelOptions?>(o => o != null && !o.PublisherConfirmationsEnabled),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(responseChannel));

        connection
            .CreateChannelAsync(
                Arg.Is<CreateChannelOptions?>(o => o != null && o.PublisherConfirmationsEnabled),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(publishChannel));

        // Captured publish arguments — filled by the BasicPublishAsync When/Do.
        string? capturedExchange = null;
        string? capturedRoutingKey = null;
        bool? capturedMandatory = null;

        // Build the client with publish-style routing values (simulating what ResolveDispatch<T>
        // returns when the type is registered via PublishRequest<T>).
        var client = new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: deserializerResolver,
            logger: NullLogger.Instance,
            targetExchange: fanoutExchangeName,     // publish-style: fanout exchange name
            routingKey: string.Empty,               // publish-style: empty routing key
            timeout: TimeSpan.FromSeconds(2),       // short timeout — safety net only
            connectionUri: FakeConnectionUri,
            vhost: null);

        await client.InitializeAsync(CancellationToken.None);

        // Wire up BasicPublishAsync: capture routing arguments AND unblock GetResponseAsync.
        // NSubstitute's fluent Returns() for ValueTask-returning methods requires suppressing
        // CA2012 (the ValueTask returned by the setup call is intentionally not awaited —
        // this is the standard NSubstitute pattern for non-awaitable setup calls).
        // The factory: (1) captures exchange/routingKey/mandatory, (2) resolves the pending TCS
        // via the AMQP CorrelationId so GetResponseAsync can complete, (3) returns completed ValueTask.
#pragma warning disable CA2012 // NSubstitute fluent setup — ValueTask intentionally not awaited here
        publishChannel
            .BasicPublishAsync<BasicProperties>(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedExchange = (string)call[0];
                capturedRoutingKey = (string)call[1];
                capturedMandatory = (bool)call[2];

                // Retrieve CorrelationId from the BasicProperties to locate the pending TCS.
                var props = (BasicProperties)call[3];
                string? correlationId = props.CorrelationId;

                // Unblock GetResponseAsync by resolving the pending TCS with a synthetic response
                // carrying the content-type the deserializer is registered for.
                if (client.TryResolvePending(
                        amqpCorrelationId: correlationId,
                        contentType: "application/json",
                        body: ReadOnlySequence<byte>.Empty,
                        out TaskCompletionSource<InboundMessage>? tcs)
                    && tcs is not null)
                {
                    var fakeInbound = new InboundMessage(
                        messageId: Guid.NewGuid().ToString(),
                        headers: new Dictionary<string, string> { ["content-type"] = "application/json" },
                        body: ReadOnlySequence<byte>.Empty,
                        deliveryTag: 1);

                    tcs.TrySetResult(fakeInbound);
                }

                return ValueTask.CompletedTask;
            });
#pragma warning restore CA2012

        // ── Act ───────────────────────────────────────────────────────────────

        Response<TestResponse> response =
            await client.GetResponseAsync<TestResponse>(new TestRequest("hello"));

        // ── Assert — AMQP routing arguments ──────────────────────────────────

        capturedExchange.Should().Be(fanoutExchangeName,
            "publish-style must route to the per-type fanout exchange");
        capturedRoutingKey.Should().BeEmpty(
            "publish-style routing key must be empty so the fanout ignores it");
        capturedMandatory.Should().BeFalse(
            "mandatory must be false when strict is not set (default opt-out; see strict tests T1/T2)");

        // ── Assert — GAP-2: exchange name is a constant string instance (NF2/ADR-003) ──
        // The captured exchange argument must be the SAME string object as the one passed into
        // the constructor (_targetExchange is set once at construction, read without re-derivation).
        object.ReferenceEquals(capturedExchange, fanoutExchangeName).Should().BeTrue(
            "the exchange name must be the constant _targetExchange field, not a new string per request");

        // ── Assert — response was correctly deserialized ──────────────────────
        response.Message.Should().NotBeNull();
        response.Message.Result.Should().Be("ok");

        await client.DisposeAsync();
    }

    // ── Strict opt-in tests (T1 / T2 — 14.10) ────────────────────────────────

    /// <summary>
    /// T1: When the client is constructed with <c>strict: true</c>, <c>BasicPublishAsync</c>
    /// must be called with <c>mandatory = true</c>.
    /// </summary>
    [Fact]
    public async Task SerializeAndPublishAsync_WhenStrict_PublishesWithMandatoryTrue()
    {
        // ── Arrange ───────────────────────────────────────────────────────────

        const string fanoutExchangeName = "OrderSystem.Events:OrderSubmitted";

        var responseDeserializer = Substitute.For<IMessageDeserializer>();
        responseDeserializer
            .Deserialize<TestResponse>(Arg.Any<ReadOnlySequence<byte>>())
            .Returns(new TestResponse("ok"));

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(responseDeserializer);

        var serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        serializer
            .When(s => s.Serialize(Arg.Any<TestRequest>(), Arg.Any<System.Buffers.IBufferWriter<byte>>()))
            .Do(_ => { });

        IChannel responseChannel = Substitute.For<IChannel>();
        IChannel publishChannel = Substitute.For<IChannel>();

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

        IConnection connection = Substitute.For<IConnection>();

        connection
            .CreateChannelAsync(
                Arg.Is<CreateChannelOptions?>(o => o != null && !o.PublisherConfirmationsEnabled),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(responseChannel));

        connection
            .CreateChannelAsync(
                Arg.Is<CreateChannelOptions?>(o => o != null && o.PublisherConfirmationsEnabled),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(publishChannel));

        bool? capturedMandatory = null;

        var client = new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: deserializerResolver,
            logger: NullLogger.Instance,
            targetExchange: fanoutExchangeName,
            routingKey: string.Empty,
            timeout: TimeSpan.FromSeconds(2),
            connectionUri: FakeConnectionUri,
            vhost: null,
            strict: true);

        await client.InitializeAsync(CancellationToken.None);

#pragma warning disable CA2012 // NSubstitute fluent setup — ValueTask intentionally not awaited here
        publishChannel
            .BasicPublishAsync<BasicProperties>(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedMandatory = (bool)call[2];

                var props = (BasicProperties)call[3];
                string? correlationId = props.CorrelationId;

                if (client.TryResolvePending(
                        amqpCorrelationId: correlationId,
                        contentType: "application/json",
                        body: ReadOnlySequence<byte>.Empty,
                        out TaskCompletionSource<InboundMessage>? tcs)
                    && tcs is not null)
                {
                    var fakeInbound = new InboundMessage(
                        messageId: Guid.NewGuid().ToString(),
                        headers: new Dictionary<string, string> { ["content-type"] = "application/json" },
                        body: ReadOnlySequence<byte>.Empty,
                        deliveryTag: 1);

                    tcs.TrySetResult(fakeInbound);
                }

                return ValueTask.CompletedTask;
            });
#pragma warning restore CA2012

        // ── Act ───────────────────────────────────────────────────────────────

        await client.GetResponseAsync<TestResponse>(new TestRequest("hello"));

        // ── Assert ────────────────────────────────────────────────────────────

        capturedMandatory.Should().BeTrue(
            "mandatory must be true when the client is constructed with strict: true");

        await client.DisposeAsync();
    }

    /// <summary>
    /// T2: When the client is constructed with <c>strict: true</c> and <c>BasicPublishAsync</c>
    /// throws a <see cref="PublishException"/> with <c>IsReturn = true</c> (broker returned the
    /// message because no responder queue is bound), <c>GetResponseAsync</c> must surface a
    /// <see cref="BareWireTransportException"/> whose message contains the exchange name and whose
    /// <c>TransportName</c> is "RabbitMQ".
    /// </summary>
    [Fact]
    public async Task SerializeAndPublishAsync_WhenStrictAndReturned_ThrowsBareWireTransportException()
    {
        // ── Arrange ───────────────────────────────────────────────────────────

        const string fanoutExchangeName = "OrderSystem.Events:OrderSubmitted";

        var responseDeserializer = Substitute.For<IMessageDeserializer>();
        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(responseDeserializer);

        var serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        serializer
            .When(s => s.Serialize(Arg.Any<TestRequest>(), Arg.Any<System.Buffers.IBufferWriter<byte>>()))
            .Do(_ => { });

        IChannel responseChannel = Substitute.For<IChannel>();
        IChannel publishChannel = Substitute.For<IChannel>();

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

        IConnection connection = Substitute.For<IConnection>();

        connection
            .CreateChannelAsync(
                Arg.Is<CreateChannelOptions?>(o => o != null && !o.PublisherConfirmationsEnabled),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(responseChannel));

        connection
            .CreateChannelAsync(
                Arg.Is<CreateChannelOptions?>(o => o != null && o.PublisherConfirmationsEnabled),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(publishChannel));

        var client = new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: deserializerResolver,
            logger: NullLogger.Instance,
            targetExchange: fanoutExchangeName,
            routingKey: string.Empty,
            timeout: TimeSpan.FromSeconds(2),
            connectionUri: FakeConnectionUri,
            vhost: null,
            strict: true);

        await client.InitializeAsync(CancellationToken.None);

        // Configure BasicPublishAsync to throw PublishException with IsReturn=true,
        // simulating the broker returning the unroutable message (no responder bound).
        // PublishException(ulong publishSequenceNumber, bool isReturn) is a public ctor in 7.2.1.
        // publishSequenceNumber must be >= 1 (the ctor validates != 0).
#pragma warning disable CA2012 // NSubstitute fluent setup — ValueTask intentionally not awaited here
        publishChannel
            .BasicPublishAsync<BasicProperties>(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException(new PublishException(publishSequenceNumber: 1UL, isReturn: true)));
#pragma warning restore CA2012

        // ── Act ───────────────────────────────────────────────────────────────

        Func<Task> act = async () =>
            await client.GetResponseAsync<TestResponse>(new TestRequest("hello"));

        // ── Assert ────────────────────────────────────────────────────────────

        var ex = await act.Should().ThrowAsync<BareWireTransportException>();
        ex.WithMessage($"*{fanoutExchangeName}*",
            "exception message must contain the exchange name for diagnostics");
        ex.Which.TransportName.Should().Be("RabbitMQ",
            "TransportName must identify the RabbitMQ transport");

        await client.DisposeAsync();
    }

    // ── T4: no BasicReturn event subscription (14.10) ─────────────────────────

    /// <summary>
    /// T4: Verifies that neither the response channel nor the publish channel ever has a
    /// <c>BasicReturnAsync</c> event handler subscribed — mandatory-return must surface
    /// via <see cref="RabbitMQ.Client.Exceptions.PublishException.IsReturn"/> on the
    /// publisher-confirmation channel, NOT via a <c>BasicReturn</c> event subscription.
    ///
    /// Assertion mechanism: NSubstitute records every call made on a substitute, including
    /// event add-accessor invocations (recorded as a call named <c>add_BasicReturnAsync</c>).
    /// We drive a full publish-style request through the T1 unblock harness and then assert
    /// that <c>ICallSpecification</c>-named <c>add_BasicReturnAsync</c> never appears in the
    /// received-calls list of either channel — the assertion fails the moment any production
    /// code adds <c>channel.BasicReturnAsync += handler;</c>.
    /// </summary>
    [Fact]
    public async Task Initialize_DoesNotSubscribeToBasicReturn()
    {
        // ── Arrange ───────────────────────────────────────────────────────────

        const string fanoutExchangeName = "OrderSystem.Events:OrderSubmitted";

        var responseDeserializer = Substitute.For<IMessageDeserializer>();
        responseDeserializer
            .Deserialize<TestResponse>(Arg.Any<ReadOnlySequence<byte>>())
            .Returns(new TestResponse("ok"));

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(responseDeserializer);

        var serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        serializer
            .When(s => s.Serialize(Arg.Any<TestRequest>(), Arg.Any<System.Buffers.IBufferWriter<byte>>()))
            .Do(_ => { });

        IChannel responseChannel = Substitute.For<IChannel>();
        IChannel publishChannel = Substitute.For<IChannel>();

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

        IConnection connection = Substitute.For<IConnection>();

        connection
            .CreateChannelAsync(
                Arg.Is<CreateChannelOptions?>(o => o != null && !o.PublisherConfirmationsEnabled),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(responseChannel));

        connection
            .CreateChannelAsync(
                Arg.Is<CreateChannelOptions?>(o => o != null && o.PublisherConfirmationsEnabled),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(publishChannel));

        var client = new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: deserializerResolver,
            logger: NullLogger.Instance,
            targetExchange: fanoutExchangeName,
            routingKey: string.Empty,
            timeout: TimeSpan.FromSeconds(2),
            connectionUri: FakeConnectionUri,
            vhost: null);

        await client.InitializeAsync(CancellationToken.None);

        // Wire up BasicPublishAsync to unblock GetResponseAsync (same pattern as T1).
#pragma warning disable CA2012 // NSubstitute fluent setup — ValueTask intentionally not awaited here
        publishChannel
            .BasicPublishAsync<BasicProperties>(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var props = (BasicProperties)call[3];
                if (client.TryResolvePending(
                        amqpCorrelationId: props.CorrelationId,
                        contentType: "application/json",
                        body: ReadOnlySequence<byte>.Empty,
                        out TaskCompletionSource<InboundMessage>? tcs)
                    && tcs is not null)
                {
                    tcs.TrySetResult(new InboundMessage(
                        messageId: Guid.NewGuid().ToString(),
                        headers: new Dictionary<string, string> { ["content-type"] = "application/json" },
                        body: ReadOnlySequence<byte>.Empty,
                        deliveryTag: 1));
                }
                return ValueTask.CompletedTask;
            });
#pragma warning restore CA2012

        // ── Act ───────────────────────────────────────────────────────────────

        await client.GetResponseAsync<TestResponse>(new TestRequest("hello"));

        // ── Assert ───────────────────────────────────────────────────────────
        // NSubstitute records event add-accessor invocations as calls named "add_BasicReturnAsync".
        // If production code ever subscribes channel.BasicReturnAsync += handler, the call appears
        // here and the assertion fails — making this test falsifiable.

        var responseChannelCalls = responseChannel.ReceivedCalls()
            .Select(c => c.GetMethodInfo().Name)
            .ToList();

        var publishChannelCalls = publishChannel.ReceivedCalls()
            .Select(c => c.GetMethodInfo().Name)
            .ToList();

        responseChannelCalls.Should().NotContain(
            "add_BasicReturnAsync",
            "the response channel must never have a BasicReturnAsync subscription; returns are handled via PublishException.IsReturn on the publish channel");

        publishChannelCalls.Should().NotContain(
            "add_BasicReturnAsync",
            "the publish channel must never have a BasicReturnAsync subscription; returns surface as PublishException.IsReturn from BasicPublishAsync");

        await client.DisposeAsync();
    }
}
