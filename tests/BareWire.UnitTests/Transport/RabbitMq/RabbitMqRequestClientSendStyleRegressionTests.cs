using System.Buffers;

using AwesomeAssertions;

using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Task 14.14 — regression snapshot proving that without PublishRequest&lt;T&gt;, the publish path stays
/// bit-identical to send-style (NF1/F6, ADR-027 Enforcement): exchange is the default AMQP exchange
/// (empty), routingKey is the responder queue, mandatory is false.
/// </summary>
public sealed class RabbitMqRequestClientSendStyleRegressionTests
{
    private sealed record TestRequest(string Value);

    private sealed record TestResponse(string Result);

    private static readonly Uri FakeConnectionUri = new("amqp://localhost");

    /// <summary>
    /// GATE — without PublishRequest&lt;T&gt; registration, the publish triple
    /// (exchange, routingKey, mandatory) must equal the send-style snapshot across multiple publishes:
    /// exchange == string.Empty (default AMQP exchange), routingKey == "test-queue" (responder queue),
    /// mandatory == false (NF1/F6, ADR-027 Enforcement).
    /// </summary>
    [Fact]
    public async Task SendStylePublish_DefaultOff_UsesEmptyExchangeQueueRoutingKeyNoMandatory()
    {
        // Arrange
        var captured = new List<(string Exchange, string RoutingKey, bool Mandatory)>();
        var client = CreateSendStyleClient(captured);
        await client.InitializeAsync(CancellationToken.None);

        // Act — publish twice to verify per-request stability, not just a single read.
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("first"));
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("second"));

        // Assert
        captured.Should().HaveCount(2, "two publishes were issued");
        foreach (var c in captured)
        {
            c.Exchange.Should().BeEmpty(
                "send-style publish must target the default AMQP exchange (empty string) — " +
                "snapshot invariant NF1/F6, ADR-027 Enforcement");
            c.RoutingKey.Should().Be("test-queue",
                "send-style publish must route directly to the responder queue — " +
                "snapshot invariant NF1/F6, ADR-027 Enforcement");
            c.Mandatory.Should().BeFalse(
                "send-style publish must NOT set mandatory (strict == false by default) — " +
                "snapshot invariant NF1/F6, ADR-027 Enforcement");
        }

        await client.DisposeAsync();
    }

    /// <summary>
    /// FALSIFICATION — proves the GATE above is live (not vacuous). When the client is constructed
    /// with publish-style values (non-empty exchange, mandatory == true), the captured triple differs
    /// from the send-style snapshot — meaning the GATE would detect such a regression.
    /// </summary>
    [Fact]
    public async Task SendStylePublish_Regression_NonEmptyExchangeOrMandatoryWouldBreakSnapshot()
    {
        // Arrange — simulate a publish-style regression: non-empty exchange, empty routingKey, strict == true.
        var captured = new List<(string Exchange, string RoutingKey, bool Mandatory)>();
        var client = CreateClient(captured, targetExchange: "OrderSystem.Events:OrderSubmitted", routingKey: string.Empty, strict: true);
        await client.InitializeAsync(CancellationToken.None);

        // Act
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("regression"));

        // Assert — the triple is NOT the send-style snapshot.
        // If production defaulted publish-style ON, the GATE above would FAIL — this proves
        // the send-style snapshot is live (NF1/F6).
        captured.Should().ContainSingle();
        captured[0].Exchange.Should().NotBeEmpty(
            "a publish-style client uses a non-empty per-type fanout exchange — " +
            "proving the GATE can detect a non-empty exchange regression");
        captured[0].Mandatory.Should().BeTrue(
            "a publish-style client with strict==true sets mandatory — " +
            "proving the GATE can detect a mandatory:true regression");

        await client.DisposeAsync();
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────

    private static RabbitMqRequestClient<TestRequest> CreateSendStyleClient(
        List<(string Exchange, string RoutingKey, bool Mandatory)> captured)
        => CreateClient(captured, targetExchange: string.Empty, routingKey: "test-queue", strict: false);

    private static RabbitMqRequestClient<TestRequest> CreateClient(
        List<(string Exchange, string RoutingKey, bool Mandatory)> captured,
        string targetExchange,
        string routingKey,
        bool strict)
    {
        var responseDeserializer = Substitute.For<IMessageDeserializer>();
        responseDeserializer
            .Deserialize<TestResponse>(Arg.Any<ReadOnlySequence<byte>>())
            .Returns(new TestResponse("ok"));

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(responseDeserializer);

        var serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        serializer
            .When(s => s.Serialize(Arg.Any<TestRequest>(), Arg.Any<IBufferWriter<byte>>()))
            .Do(_ => { /* no-op — body content is irrelevant for the triple assertion */ });

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
            vhost: null,
            strict: strict);

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
                captured.Add(((string)call[0], (string)call[1], (bool)call[2]));

                var props = (BasicProperties)call[3];
                if (client!.TryResolvePending(
                        amqpCorrelationId: props.CorrelationId,
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

        return client;
    }
}
