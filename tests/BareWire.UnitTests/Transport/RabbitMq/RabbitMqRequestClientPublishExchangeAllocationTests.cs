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
/// Task 14.8 — allocation gate proving the publish-style per-type exchange NAME is computed once
/// (cached field, read by reference) and is NOT allocated per message on the publish path.
///
/// <para><b>What this gate enforces (ADR-027 Enforcement / ADR-003 NF2):</b>
/// The per-type fanout exchange name is set once at construction (<c>_targetExchange</c>,
/// <c>RabbitMqRequestClient&lt;TRequest&gt;</c> ctor) and handed to <c>BasicPublishAsync</c> by
/// reference — zero allocation of the exchange-name STRING per request. A regression that
/// re-derives the name per call (e.g. <c>new string(...)</c>, <c>string.Concat</c>, interpolation)
/// breaks the "computed once" invariant.</para>
///
/// <para><b>Narrowed scope (explicit):</b> this gate asserts ONLY the exchange-name string on the
/// publish path. It deliberately makes NO claim about the response path — <c>args.Body.ToArray()</c>
/// and the per-response header dictionary in the consumer callback DO allocate per response (×N under
/// broadcast), and <c>requestId.ToString()</c> + the per-request envelope also allocate. None of those
/// are the exchange name and none are in scope here (ADR-027: "the gate measures publish; it makes
/// no claim about the response path").</para>
///
/// <para><b>Why reference identity instead of a GC byte-delta (task-verify GAP-2 / OQ2 / OQ3):</b>
/// A single-call <c>GC.GetAllocatedBytesForCurrentThread()</c> delta cannot isolate the exchange-name
/// cost — <c>BasicProperties</c> construction and <c>requestId.ToString()</c> allocate inside the same
/// measured window (and the async state machine boxes in Debug), so a bare <c>delta == 0</c> assertion
/// is not measurable. Reference identity is the deterministic, GC-noise-free proof of "computed once":
/// if the captured exchange argument is the SAME object the caller passed to the constructor, the name
/// was not re-derived (and therefore not re-allocated) on the publish path.</para>
///
/// <para><b>Anti-vacuity (task-verify GAP-2):</b> the assertion is made against a RUNTIME, NON-INTERNED
/// string instance — never against a <c>const</c>/literal. A literal is interned, so a regression that
/// reconstructs an equal-valued string could still satisfy <c>ReferenceEquals</c> against a literal and
/// the gate would be vacuous. Building the expected name at runtime (<c>new string(char[])</c>) yields a
/// distinct heap instance: only the genuine cached-field read can be reference-equal to it.
/// <see cref="PublishStylePublish_ExchangeName_Falsification_PerCallReconstructionFailsReferenceIdentity"/>
/// proves the gate is live by reconstructing the name per call and showing the identity check fails.</para>
/// </summary>
public sealed class RabbitMqRequestClientPublishExchangeAllocationTests
{
    private sealed record TestRequest(string Value);

    private sealed record TestResponse(string Result);

    private static readonly Uri FakeConnectionUri = new("amqp://localhost");

    /// <summary>
    /// Builds the per-type fanout exchange name as a fresh, non-interned heap string. Using
    /// <c>new string(char[])</c> guarantees the instance is NOT the interned literal, so a
    /// <see cref="object.ReferenceEquals(object, object)"/> match against it can only be produced
    /// by the cached <c>_targetExchange</c> field being read by reference (anti-vacuity, GAP-2).
    /// </summary>
    private static string NewNonInternedExchangeName()
        => new("OrderSystem.Events:OrderSubmitted".ToCharArray());

    /// <summary>
    /// GATE — the exchange name captured at <c>BasicPublishAsync</c> is the SAME object instance the
    /// caller passed to the constructor, across multiple publishes. Proves the name is read once from
    /// the cached field, never re-derived/re-allocated per request (ADR-027 Enforcement, ADR-003 NF2).
    /// </summary>
    [Fact]
    public async Task PublishStylePublish_ExchangeName_IsSameCachedInstanceAcrossRequests()
    {
        // Arrange — a RUNTIME non-interned instance (anti-vacuity: not a literal/const).
        string expectedExchange = NewNonInternedExchangeName();
        object.ReferenceEquals(expectedExchange, "OrderSystem.Events:OrderSubmitted")
            .Should().BeFalse("test guard: the expected name must be a non-interned runtime instance");

        var captured = new List<string>();
        var client = CreatePublishStyleClient(expectedExchange, captured, reconstructExchangePerCall: false);

        await client.InitializeAsync(CancellationToken.None);

        // Act — publish twice so the assertion covers per-request stability, not just a single read.
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("first"));
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("second"));

        // Assert — every captured exchange is the SAME object as the constructor argument.
        captured.Should().HaveCount(2, "two publishes were issued");
        foreach (string exchange in captured)
        {
            object.ReferenceEquals(exchange, expectedExchange).Should().BeTrue(
                "the exchange name must be the cached _targetExchange field read by reference, " +
                "not a new string allocated per request (ADR-027 Enforcement, ADR-003 NF2)");
        }

        // And the two captured references are identical to each other — no per-request churn.
        object.ReferenceEquals(captured[0], captured[1]).Should().BeTrue(
            "the same cached field instance must be used for every publish on the same client");

        await client.DisposeAsync();
    }

    /// <summary>
    /// FALSIFICATION — proves the gate is live (not vacuous). When the publish path is forced to
    /// reconstruct the exchange name per call (simulating a "computed per message" regression), the
    /// reference-identity assertion of the gate above must FAIL. If this test cannot make the gate
    /// fail, the gate proves nothing.
    /// </summary>
    [Fact]
    public async Task PublishStylePublish_ExchangeName_Falsification_PerCallReconstructionFailsReferenceIdentity()
    {
        // Arrange — same constructor instance, but the stub reconstructs the captured name per call,
        // emulating a per-message string allocation regression on the publish path.
        string expectedExchange = NewNonInternedExchangeName();

        var captured = new List<string>();
        var client = CreatePublishStyleClient(expectedExchange, captured, reconstructExchangePerCall: true);

        await client.InitializeAsync(CancellationToken.None);

        // Act
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("regression"));

        // Assert — value still equals (routing unaffected) ...
        captured.Should().ContainSingle();
        captured[0].Should().Be(expectedExchange, "a re-derived name still carries the same value");

        // ... but reference identity is BROKEN — exactly what the live gate must catch.
        object.ReferenceEquals(captured[0], expectedExchange).Should().BeFalse(
            "falsification: a per-call reconstructed exchange name must NOT be reference-equal to the " +
            "cached instance — proving the IsSameCachedInstanceAcrossRequests gate can detect a " +
            "per-message allocation regression (i.e. the gate is not vacuous)");

        await client.DisposeAsync();
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a publish-style <see cref="RabbitMqRequestClient{TRequest}"/> wired to NSubstitute
    /// channels. The publish channel's <c>BasicPublishAsync</c> callback captures the exchange
    /// argument and resolves the pending TCS so <c>GetResponseAsync</c> completes without a broker.
    ///
    /// <para>NSubstitute proxies allocate, but they run OUTSIDE the asserted property — this gate
    /// asserts reference identity of the captured string, which is immune to allocation noise. The
    /// captured value is exactly the object the production code passed to <c>BasicPublishAsync</c>.</para>
    /// </summary>
    private static RabbitMqRequestClient<TestRequest> CreatePublishStyleClient(
        string targetExchange,
        List<string> capturedExchanges,
        bool reconstructExchangePerCall)
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
            .Do(_ => { /* no-op — body content is irrelevant for the exchange-name assertion */ });

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
            routingKey: string.Empty,
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
                var exchangeArg = (string)call[0];

                // Falsification toggle: reconstruct the name per call to simulate a per-message
                // string-allocation regression (a distinct heap instance with the same value).
                capturedExchanges.Add(
                    reconstructExchangePerCall ? new string(exchangeArg.ToCharArray()) : exchangeArg);

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
