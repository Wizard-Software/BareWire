using System.Buffers;
using System.Text.Json;

using AwesomeAssertions;

using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Interop.MassTransit;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Internal;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Task 14.12 — unit tests proving that a publish-style request client emits a MassTransit
/// envelope whose <c>destinationAddress</c> field equals the per-type fanout exchange URI
/// (ADR-027, D9).
///
/// <para><b>D9 decision (URI, not null):</b> under publish-style routing (Feature 14) the
/// factory's <c>ResolveDispatch&lt;T&gt;</c> sets <c>_targetExchange</c> to the fanout exchange
/// name ("Namespace:TypeName") and leaves <c>_routingKey</c> empty. The existing fallback in
/// <c>InitializeAsync</c> therefore resolves <c>_destinationAddress</c> to the fanout exchange
/// URI — matching MassTransit <c>Publish</c> semantics. A MT responder reading the envelope sees
/// <c>destinationAddress</c> as "where this was published to". Response routing does NOT use it
/// (relies on <c>responseAddress</c> / AMQP <c>ReplyTo</c>).</para>
///
/// <para><b>Two conscious differences from reference pattern (14.8 allocation gate):</b>
/// <list type="number">
///   <item>Real <see cref="MassTransitEnvelopeSerializer"/> — the reference injects a no-op
///   <c>Substitute.For&lt;IMessageSerializer&gt;()</c> that does NOT implement
///   <see cref="IRequestEnvelopeSerializer"/>, so the envelope branch is never entered and
///   <c>destinationAddress</c> is never emitted. This test requires the real serializer so the
///   envelope (including <c>destinationAddress</c>) is actually written to the body.</item>
///   <item>Body captured as <c>call[4]</c> — the reference reads only <c>call[0]</c> (exchange
///   name string). Here the full envelope JSON is captured from the 5th positional argument
///   (<c>ReadOnlyMemory&lt;byte&gt;</c>) and parsed to read the <c>destinationAddress</c>
///   field.</item>
/// </list>
/// </para>
/// </summary>
public sealed class RabbitMqRequestClientPublishStyleDestinationAddressTests
{
    private sealed record TestRequest(string Value);

    private sealed record TestResponse(string Result);

    private static readonly Uri FakeConnectionUri = new("amqp://localhost");

    /// <summary>
    /// GATE — a publish-style client emits a MassTransit envelope whose
    /// <c>destinationAddress</c> equals the per-type fanout exchange URI built by the same
    /// production builder (<see cref="RabbitMqEndpointAddress.Build"/>), confirming D9 (ADR-027).
    ///
    /// <para><b>RabbitMqEndpointAddress visibility:</b> <c>RabbitMqEndpointAddress</c> is
    /// <see langword="internal"/> in <c>BareWire.Transport.RabbitMQ</c> but is accessible here
    /// via the <c>InternalsVisibleTo("BareWire.UnitTests")</c> declaration in the project file.
    /// The assertion calls the same production builder so no URI format is hand-duplicated in
    /// the test.</para>
    /// </summary>
    [Fact]
    public async Task PublishStyle_DestinationAddress_IsFanoutExchangeUri()
    {
        // Arrange
        const string targetExchange = "OrderSystem.Events:OrderSubmitted";
        var capturedBody = new List<ReadOnlyMemory<byte>>();

        var client = CreateClient(
            targetExchange: targetExchange,
            routingKey: string.Empty,
            capturedBodies: capturedBody);

        await client.InitializeAsync(CancellationToken.None);

        // Act — issue one request; TryResolvePending completes GetResponseAsync without a broker.
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("hello"));

        // Assert — exactly one publish; body must not be empty (real serializer wrote an envelope).
        capturedBody.Should().ContainSingle("exactly one publish was issued");
        capturedBody[0].Length.Should().BeGreaterThan(0, "the real serializer must have written the envelope");

        string destinationAddress = ReadDestinationAddress(capturedBody[0]);
        string expectedUri = RabbitMqEndpointAddress.Build(FakeConnectionUri, vhost: null, targetExchange, temporary: false);

        destinationAddress.Should().Be(expectedUri,
            "publish-style destinationAddress must equal the per-type fanout exchange URI (ADR-027 D9)");

        await client.DisposeAsync();
    }

    /// <summary>
    /// FALSIFICATION (anti-vacuity) — proves that the assertion in
    /// <see cref="PublishStyle_DestinationAddress_IsFanoutExchangeUri"/> is sensitive.
    /// A send-style client (different <c>targetExchange</c>, non-empty <c>routingKey</c>)
    /// produces a <c>destinationAddress</c> built from its own exchange name, which must differ
    /// from the publish-style fanout exchange URI. If this test fails, the gate above could not
    /// have caught a regression that confuses the two paths.
    /// </summary>
    [Fact]
    public async Task PublishStyle_DestinationAddress_Falsification_DiffersFromSendStyleRouting()
    {
        // Arrange — send-style: targetExchange and routingKey are both non-empty (different names).
        const string sendExchange = "some-exchange";
        const string sendRoutingKey = "some-queue";
        var capturedBody = new List<ReadOnlyMemory<byte>>();

        var client = CreateClient(
            targetExchange: sendExchange,
            routingKey: sendRoutingKey,
            capturedBodies: capturedBody);

        await client.InitializeAsync(CancellationToken.None);

        // Act
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("send"));

        // Read the destinationAddress actually emitted by this send-style client.
        capturedBody.Should().ContainSingle();
        string actualDestinationAddress = ReadDestinationAddress(capturedBody[0]);

        // The publish-style fanout URI for "OrderSystem.Events:OrderSubmitted" (a completely
        // different exchange name) must NOT match — proving the gate is live (not vacuous).
        string publishStyleFanoutUri = RabbitMqEndpointAddress.Build(
            FakeConnectionUri, vhost: null, "OrderSystem.Events:OrderSubmitted", temporary: false);

        actualDestinationAddress.Should().NotBe(publishStyleFanoutUri,
            "the send-style destinationAddress (built from 'some-exchange') must differ from the " +
            "publish-style fanout exchange URI — proving the gate in the main test can detect a " +
            "regression that confuses publish-style and send-style destination addresses");

        await client.DisposeAsync();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the captured MassTransit envelope JSON and returns the <c>destinationAddress</c>
    /// string value.
    /// </summary>
    private static string ReadDestinationAddress(ReadOnlyMemory<byte> body)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        bool found = doc.RootElement.TryGetProperty("destinationAddress", out JsonElement element);

        found.Should().BeTrue(
            "the MassTransit envelope must contain a 'destinationAddress' field; " +
            "if missing, the real MassTransitEnvelopeSerializer is not wired or _destinationAddress is null/empty");

        return element.GetString()
            ?? throw new InvalidOperationException("destinationAddress field is present but null in the envelope JSON.");
    }

    /// <summary>
    /// Builds a <see cref="RabbitMqRequestClient{TRequest}"/> wired to NSubstitute channels.
    /// Uses a real <see cref="MassTransitEnvelopeSerializer"/> so the envelope (including
    /// <c>destinationAddress</c>) is actually written to the publish body.
    ///
    /// <para>The <c>BasicPublishAsync</c> callback captures the raw body bytes and resolves the
    /// pending <see cref="TaskCompletionSource{T}"/> so that <c>GetResponseAsync</c> completes
    /// without a live broker.</para>
    /// </summary>
    private static RabbitMqRequestClient<TestRequest> CreateClient(
        string targetExchange,
        string routingKey,
        List<ReadOnlyMemory<byte>> capturedBodies)
    {
        // Conscious difference 1: real serializer so envelope + destinationAddress are emitted.
        var serializer = new MassTransitEnvelopeSerializer();

        var responseDeserializer = Substitute.For<IMessageDeserializer>();
        responseDeserializer
            .Deserialize<TestResponse>(Arg.Any<ReadOnlySequence<byte>>())
            .Returns(new TestResponse("ok"));

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(responseDeserializer);

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
            targetExchange: targetExchange,
            routingKey: routingKey,
            timeout: TimeSpan.FromSeconds(2),
            connectionUri: FakeConnectionUri,
            vhost: null);

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
                // Conscious difference 2: capture call[4] (body), not call[0] (exchange name).
                // The body is a ReadOnlyMemory<byte> rented from ArrayPool; copy it before the
                // pool buffer is returned after BasicPublishAsync completes.
                var body = (ReadOnlyMemory<byte>)call[4];
                capturedBodies.Add(body.ToArray()); // materialise before pool return

                var props = (BasicProperties)call[3];
                if (client!.TryResolvePending(
                        amqpCorrelationId: props.CorrelationId,
                        contentType: serializer.ContentType,
                        body: ReadOnlySequence<byte>.Empty,
                        out TaskCompletionSource<InboundMessage>? tcs)
                    && tcs is not null)
                {
                    var fakeInbound = new InboundMessage(
                        messageId: Guid.NewGuid().ToString(),
                        headers: new Dictionary<string, string> { ["content-type"] = serializer.ContentType },
                        body: ReadOnlySequence<byte>.Empty,
                        deliveryTag: 1);

                    tcs.TrySetResult(fakeInbound);
                }

                return ValueTask.CompletedTask;
            });
#pragma warning restore CA2012

        return client;
    }
}
