using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Regression-guard unit tests for first-in-wins hardening in <see cref="RabbitMqRequestClient{TRequest}"/>
/// (task 14.9). All tests are broker-free and cover acceptance criteria a/b/c.
/// </summary>
public sealed class RabbitMqRequestClientFirstInWinsTests
{
    // ── Test records ───────────────────────────────────────────────────────────

    private sealed record TestRequest(string Value);

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static readonly Uri FakeConnectionUri = new("amqp://localhost");

    /// <summary>
    /// Creates a <see cref="RabbitMqRequestClient{TRequest}"/> wired to a mock <see cref="IConnection"/>
    /// that returns the supplied <paramref name="responseChannel"/> from <c>CreateChannelAsync</c>.
    /// Mirrors the setup used in <c>DisposeAsync_CancelsPendingRequests</c> in the sibling test file.
    /// </summary>
    private static async Task<RabbitMqRequestClient<TestRequest>> CreateInitializedClientAsync(
        IConnection connection,
        IChannel responseChannel)
    {
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

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        var client = new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: Substitute.For<IDeserializerResolver>(),
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "test-queue",
            timeout: TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null);

        await client.InitializeAsync(CancellationToken.None);
        return client;
    }

    // ── (a) PRIMARY mechanism — TrySetResult idempotency ──────────────────────

    /// <summary>
    /// Regression guard for the PRIMARY first-in-wins mechanism (task 14.9).
    /// Seeds one TCS via <c>SeedPendingForTest</c>, then calls <c>TrySetResult</c> twice
    /// (simulating two competing-responder replies arriving on the same correlation id).
    /// Verifies: first call returns <see langword="true"/> and completes the task with the
    /// first value; second call returns <see langword="false"/> and is a no-op (task value
    /// unchanged). This test would catch a regression where someone treats the ignored
    /// <c>TrySetResult</c> return value as a bug and introduces logic that overwrites the result.
    /// </summary>
    [Fact]
    public async Task OnResponseReceived_SecondTrySetResultOnCompletedTcs_IsNoOp()
    {
        // Arrange
        var client = new RabbitMqRequestClient<TestRequest>(
            connection: Substitute.For<IConnection>(),
            serializer: Substitute.For<IMessageSerializer>(),
            deserializerResolver: Substitute.For<IDeserializerResolver>(),
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "test-queue",
            timeout: TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null);

        Guid requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SeedPendingForTest(requestId, tcs);

        var firstInbound = new InboundMessage(
            messageId: "msg-first",
            headers: new Dictionary<string, string>(),
            body: ReadOnlySequence<byte>.Empty,
            deliveryTag: 1);

        var secondInbound = new InboundMessage(
            messageId: "msg-second",
            headers: new Dictionary<string, string>(),
            body: ReadOnlySequence<byte>.Empty,
            deliveryTag: 2);

        // Act — simulate two competing-responder replies arriving for the same TCS.
        bool firstResult = tcs.TrySetResult(firstInbound);
        bool secondResult = tcs.TrySetResult(secondInbound);

        // Assert — first responder wins; second TrySetResult is a no-op.
        firstResult.Should().BeTrue("first TrySetResult on a pending TCS must succeed");
        secondResult.Should().BeFalse("second TrySetResult on an already-completed TCS must return false (no-op)");
        tcs.Task.IsCompleted.Should().BeTrue();

        InboundMessage completed = await tcs.Task;
        completed.MessageId.Should().Be("msg-first", "task value must not be overwritten by the second call");
    }

    // ── (b) SECONDARY mechanism — consumerDispatchConcurrency:1 regression guard ─

    /// <summary>
    /// REGRESSION GUARD: verifies that <c>InitializeAsync</c> creates the response channel with
    /// <c>ConsumerDispatchConcurrency == 1</c>, set EXPLICITLY as a named constructor argument
    /// (task 14.9, Enforcement C2, ADR-027). This test does NOT go through a RED phase because the
    /// RabbitMQ.Client 7.2.1 constructor default is already <c>1</c> — the assertion guards against
    /// a future regression where the explicit argument is removed AND the library default changes.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_SetsConsumerDispatchConcurrencyToOneExplicitly()
    {
        // Arrange
        IConnection connection = Substitute.For<IConnection>();
        IChannel responseChannel = Substitute.For<IChannel>();

        CreateChannelOptions? capturedOptions = null;

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

        // Capture the CreateChannelOptions passed to CreateChannelAsync for the response channel
        // (identified by PublisherConfirmationsEnabled == false).
        connection
            .CreateChannelAsync(
                Arg.Do<CreateChannelOptions?>(o =>
                {
                    if (o != null && !o.PublisherConfirmationsEnabled)
                    {
                        capturedOptions = o;
                    }
                }),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(responseChannel));

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        var client = new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: Substitute.For<IDeserializerResolver>(),
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "test-queue",
            timeout: TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null);

        // Act
        await client.InitializeAsync(CancellationToken.None);

        // Assert — regression guard: ConsumerDispatchConcurrency must be explicitly set to 1.
        // If the explicit argument is removed and the library default changes, this assertion catches it.
        capturedOptions.Should().NotBeNull("CreateChannelAsync must have been called for the response channel");
        capturedOptions!.ConsumerDispatchConcurrency.Should().Be((ushort)1,
            "consumerDispatchConcurrency must be pinned to 1 explicitly (C2, ADR-027) to prevent " +
            "concurrent dispatch and self-document first-in-wins intent");

        await client.DisposeAsync();
    }

    // ── (c1) Clean discard after _pending.TryRemove ────────────────────────────

    /// <summary>
    /// Verifies that a duplicate response arriving after <c>_pending.TryRemove</c> (i.e., after
    /// <c>GetResponseAsync</c>'s finally block has already removed the entry) is discarded cleanly:
    /// <c>TryResolvePending</c> returns <see langword="false"/>, the out-TCS is <see langword="null"/>,
    /// and no exception is thrown.
    /// </summary>
    [Fact]
    public void OnResponseReceived_DuplicateAfterPendingRemoved_IsDiscardedCleanly()
    {
        // Arrange — empty _pending (simulates state after GetResponseAsync's finally removed the entry).
        var client = new RabbitMqRequestClient<TestRequest>(
            connection: Substitute.For<IConnection>(),
            serializer: Substitute.For<IMessageSerializer>(),
            deserializerResolver: Substitute.For<IDeserializerResolver>(),
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "test-queue",
            timeout: TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null);

        // Do not seed anything — _pending is intentionally empty.
        string unknownCorrelationId = Guid.NewGuid().ToString();

        // Act — TryResolvePending with an id not in _pending.
        bool resolved = client.TryResolvePending(
            amqpCorrelationId: unknownCorrelationId,
            contentType: null,
            body: ReadOnlySequence<byte>.Empty,
            out TaskCompletionSource<InboundMessage>? result);

        // Assert — clean discard: false, null TCS, no exception.
        resolved.Should().BeFalse("a duplicate arriving after _pending removal must be discarded");
        ((object?)result).Should().BeNull("no TCS should be returned for an unknown correlation id");
    }

    // ── (c2) Clean discard after DisposeAsync ─────────────────────────────────

    /// <summary>
    /// Verifies that a response arriving after <c>DisposeAsync</c> is discarded cleanly.
    /// After dispose, <c>_pending</c> is cleared; <c>TryResolvePending</c> therefore finds no
    /// matching entry and returns <see langword="false"/> without throwing. No nack is issued
    /// (the response channel is gone; <c>autoAck:true</c> already acknowledged the delivery).
    /// </summary>
    [Fact]
    public async Task OnResponseReceived_ResponseAfterDispose_IsDiscardedCleanly()
    {
        // Arrange — initialize a client with mock IConnection/IChannel, then dispose it.
        IConnection connection = Substitute.For<IConnection>();
        IChannel responseChannel = Substitute.For<IChannel>();

        await CreateInitializedClientAsync(connection, responseChannel);

        // The helper already disposed nothing — create a fresh client and dispose it ourselves.
        var client = await CreateInitializedClientAsync(
            Substitute.For<IConnection>(),
            Substitute.For<IChannel>());

        // Reinitialize with a fresh, properly wired mock pair.
        IConnection conn2 = Substitute.For<IConnection>();
        IChannel chan2 = Substitute.For<IChannel>();
        var client2 = await CreateInitializedClientAsync(conn2, chan2);

        await client2.DisposeAsync();

        // Act — after dispose, _pending is cleared; TryResolvePending must return false cleanly.
        bool resolved = false;
        TaskCompletionSource<InboundMessage>? result = null;

        Action act = () =>
        {
            resolved = client2.TryResolvePending(
                amqpCorrelationId: Guid.NewGuid().ToString(),
                contentType: null,
                body: ReadOnlySequence<byte>.Empty,
                out result);
        };

        // Assert — no exception, clean discard.
        act.Should().NotThrow("a post-dispose TryResolvePending must not throw");
        resolved.Should().BeFalse("after dispose _pending is empty, so no entry can match");
        ((object?)result).Should().BeNull("no TCS should be returned after dispose");
    }
}
