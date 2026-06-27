using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire.Serialization.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

// ── Message types for untyped dispatch tests ──────────────────────────────────────────────────────
// Declared as public records so ConsumerInvokerFactory can resolve them generically at startup.

public sealed record UntypedOrder(string OrderId);

public sealed record UntypedEvent(string EventId);

// ── Consumer types ────────────────────────────────────────────────────────────────────────────────

public sealed class UntypedOrderAcceptConsumer(List<string> log) : IConsumer<UntypedOrder>
{
    public Task ConsumeAsync(ConsumeContext<UntypedOrder> context)
    {
        log.Add($"AcceptUntyped:{context.Message.OrderId}");
        return Task.CompletedTask;
    }
}

public sealed class UntypedOrderNoAcceptConsumer(List<string> log) : IConsumer<UntypedOrder>
{
    public Task ConsumeAsync(ConsumeContext<UntypedOrder> context)
    {
        log.Add($"NoAcceptUntyped:{context.Message.OrderId}");
        return Task.CompletedTask;
    }
}

public sealed class UntypedOrderSpecificConsumer(List<string> log) : IConsumer<UntypedOrder>
{
    public Task ConsumeAsync(ConsumeContext<UntypedOrder> context)
    {
        log.Add("Specific");
        return Task.CompletedTask;
    }
}

public sealed class UntypedOrderWildcardConsumer(List<string> log) : IConsumer<UntypedOrder>
{
    public Task ConsumeAsync(ConsumeContext<UntypedOrder> context)
    {
        log.Add("Wildcard");
        return Task.CompletedTask;
    }
}

public sealed class UntypedOrderTieConsumerA(List<string> log) : IConsumer<UntypedOrder>
{
    public Task ConsumeAsync(ConsumeContext<UntypedOrder> context)
    {
        log.Add("TieA");
        return Task.CompletedTask;
    }
}

public sealed class UntypedOrderTieConsumerB(List<string> log) : IConsumer<UntypedOrder>
{
    public Task ConsumeAsync(ConsumeContext<UntypedOrder> context)
    {
        log.Add("TieB");
        return Task.CompletedTask;
    }
}

public sealed class UntypedEventAcceptConsumer(List<string> log) : IConsumer<UntypedEvent>
{
    public Task ConsumeAsync(ConsumeContext<UntypedEvent> context)
    {
        log.Add($"Event:{context.Message.EventId}");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Tests for ADR-030 layer 3 type-less raw-first dispatch (<c>SelectUntypedAsync</c>):
/// gating (<c>AcceptUntyped()</c> + non-empty pattern required), most-specific-wins selection,
/// type-less hardening (payload size cap, MaxDepth regression, no-polymorphic regression),
/// fallthrough to layer 4 when no candidate, and guard regression (legacy path unchanged).
/// </summary>
public sealed class ReceiveEndpointRunnerUntypedDispatchTests
{
    private const string EndpointName = "untyped-dispatch-test-endpoint";

    // ── Test 1: happy path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchMessageAsync_TypeLessDelivery_PatternMatchesAcceptUntypedConsumer_SelectsConsumerAndDeserializesRawFirst()
    {
        // Arrange — one AcceptUntyped consumer with pattern "orders.*"; delivery has no BW-MessageType
        // but a matching BW-RoutingKey. The mock deserializer returns a valid UntypedOrder.
        List<string> log = [];
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(
                    typeof(UntypedOrderAcceptConsumer),
                    typeof(UntypedOrder),
                    RoutingKeys: ["orders.*"],
                    AcceptUntyped: true),
            ],
            new Dictionary<Type, object>
            {
                [typeof(UntypedOrderAcceptConsumer)] = new UntypedOrderAcceptConsumer(log),
            },
            CreateMockDeserializerResolver(new UntypedOrder("ord-42")),
            NullLogger<ReceiveEndpointRunner>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(
                new() { ["BW-RoutingKey"] = "orders.created" }), // no BW-MessageType
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — the AcceptUntyped consumer received the deserialized message.
        log.Should().ContainSingle().Which.Should().Be("AcceptUntyped:ord-42");
    }

    // ── Test 2: security gating — AcceptUntyped() is mandatory ───────────────────────────────────

    [Fact]
    public async Task DispatchMessageAsync_TypeLessDelivery_PatternConsumerWithoutAcceptUntyped_NotSelected()
    {
        // Arrange — consumer has a matching pattern "orders.*" but AcceptUntyped == false (default).
        // A type-less delivery MUST NOT be routed to this consumer (ADR-FIX-1 gating).
        List<string> log = [];
        CapturingLogger logger = new();
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(
                    typeof(UntypedOrderNoAcceptConsumer),
                    typeof(UntypedOrder),
                    RoutingKeys: ["orders.*"],
                    AcceptUntyped: false), // explicit false — security gate must prevent selection
            ],
            new Dictionary<Type, object>
            {
                [typeof(UntypedOrderNoAcceptConsumer)] = new UntypedOrderNoAcceptConsumer(log),
            },
            CreateMockDeserializerResolver(new UntypedOrder("ord-1")),
            logger);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new() { ["BW-RoutingKey"] = "orders.created" }), // type-less delivery
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — consumer was NOT called (security gate blocked it); dispatched == false → "No consumer matched".
        log.Should().BeEmpty("consumer without AcceptUntyped() must never receive a type-less delivery");
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("No consumer matched"));
    }

    // ── Test 3: most-specific-wins among AcceptUntyped candidates ────────────────────────────────

    [Fact]
    public async Task DispatchMessageAsync_TypeLessDelivery_MultipleAcceptUntypedMatch_SelectsMostSpecific()
    {
        // Arrange — two AcceptUntyped consumers: "orders.eu.*" (more specific) and "orders.#" (wider).
        // Both match "orders.eu.created" but the specific one must win (D5 metric).
        List<string> log = [];
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(
                    typeof(UntypedOrderWildcardConsumer),
                    typeof(UntypedOrder),
                    RoutingKeys: ["orders.#"],
                    AcceptUntyped: true),
                new ConsumerRegistration(
                    typeof(UntypedOrderSpecificConsumer),
                    typeof(UntypedOrder),
                    RoutingKeys: ["orders.eu.*"],
                    AcceptUntyped: true),
            ],
            new Dictionary<Type, object>
            {
                [typeof(UntypedOrderWildcardConsumer)] = new UntypedOrderWildcardConsumer(log),
                [typeof(UntypedOrderSpecificConsumer)] = new UntypedOrderSpecificConsumer(log),
            },
            CreateMockDeserializerResolver(new UntypedOrder("ord-1")),
            NullLogger<ReceiveEndpointRunner>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new() { ["BW-RoutingKey"] = "orders.eu.created" }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — only the more specific consumer fires.
        log.Should().ContainSingle().Which.Should().Be("Specific");
    }

    // ── Test 4: unresolvable tie → first-registered + warning ────────────────────────────────────

    [Fact]
    public async Task DispatchMessageAsync_TypeLessDelivery_UnresolvableSpecificityTie_FirstRegisteredAndWarns()
    {
        // Arrange — two AcceptUntyped consumers with the SAME pattern → identical specificity.
        List<string> log = [];
        CapturingLogger logger = new();
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(
                    typeof(UntypedOrderTieConsumerA),
                    typeof(UntypedOrder),
                    RoutingKeys: ["orders.*"],
                    AcceptUntyped: true),
                new ConsumerRegistration(
                    typeof(UntypedOrderTieConsumerB),
                    typeof(UntypedOrder),
                    RoutingKeys: ["orders.*"],
                    AcceptUntyped: true),
            ],
            new Dictionary<Type, object>
            {
                [typeof(UntypedOrderTieConsumerA)] = new UntypedOrderTieConsumerA(log),
                [typeof(UntypedOrderTieConsumerB)] = new UntypedOrderTieConsumerB(log),
            },
            CreateMockDeserializerResolver(new UntypedOrder("ord-1")),
            logger);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        const string tieKey = "orders.placed";
        await writer.WriteAsync(
            MakeMessage(new() { ["BW-RoutingKey"] = tieKey }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — first-registered consumer wins; ambiguity warning fired; raw key NOT in any log entry.
        log.Should().ContainSingle().Which.Should().Be("TieA");
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Ambiguous routing-key match"));

        // ADR-030 §Security: the raw routing-key value must never appear in any log message.
        logger.Entries.Should().NotContain(e => e.Message.Contains(tieKey));
    }

    // ── Test 5: payload size guard — no deserialization, no leak ─────────────────────────────────

    [Fact]
    public async Task DispatchMessageAsync_TypeLessDelivery_PayloadExceedsMaxSize_NotDeserializedAndDoesNotLeak()
    {
        // Arrange — construct a payload that exceeds MaxUntypedPayloadBytes (1 MiB) so the size guard
        // fires before the deserializer is called.
        const string sensitiveRoutingKey = "orders.sensitive";
        List<string> log = [];
        CapturingLogger logger = new();

        // Build a payload that is just over 1 MiB (1 MiB + 1 byte).
        byte[] oversizedPayload = new byte[1 * 1024 * 1024 + 1];
        // Fill with ASCII zeros so it looks like a valid JSON string prefix (the deserializer must NOT
        // be called; we only care that the guard fires before any attempt to deserialize).
        oversizedPayload.AsSpan().Fill((byte)'0');

        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(
                    typeof(UntypedOrderAcceptConsumer),
                    typeof(UntypedOrder),
                    RoutingKeys: ["orders.*"],
                    AcceptUntyped: true),
            ],
            new Dictionary<Type, object>
            {
                [typeof(UntypedOrderAcceptConsumer)] = new UntypedOrderAcceptConsumer(log),
            },
            CreateMockDeserializerResolver(new UntypedOrder("should-not-be-deserialized")),
            logger);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(
                new() { ["BW-RoutingKey"] = sensitiveRoutingKey },
                body: oversizedPayload),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — consumer was NOT called (size guard fired before deserialization).
        log.Should().BeEmpty("oversized payload must be rejected before deserialization");

        // A warning about oversized payload should have fired.
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("exceeds the maximum allowed size"));

        // ADR-030 §Security: neither the routing key nor any payload fragment must appear in any log entry.
        logger.Entries.Should().NotContain(e => e.Message.Contains(sensitiveRoutingKey),
            "raw routing-key must never be logged");
        logger.Entries.Should().NotContain(e => e.Message.Contains("should-not-be-deserialized"),
            "payload content must never be logged");
    }

    // ── Test 6: MaxDepth regression lock (REAL deserializer required — GAP-2) ────────────────────

    [Fact]
    public async Task DispatchMessageAsync_TypeLessDelivery_DepthBeyondMaxDepth_DeserializationRejected()
    {
        // Arrange — build JSON nested beyond STJ's default MaxDepth (64). The real
        // SystemTextJsonRawDeserializer must reject it with BareWireSerializationException, proving
        // that MaxDepth has NOT been raised on the type-less path (regression lock, GAP-2).
        // A mock deserializer would make this test vacuous — we MUST use the real one.
        List<string> log = [];
        CapturingLogger logger = new();

        // Produce JSON nested 65 levels deep (one beyond the STJ default of 64).
        // {"a":{"a":{"a": ... "value" ... }}} — 65 levels of nesting.
        StringBuilder sb = new();
        for (int i = 0; i < 65; i++) sb.Append("""{"a":""");
        sb.Append("\"deep\"");
        for (int i = 0; i < 65; i++) sb.Append('}');
        byte[] deepJson = Encoding.UTF8.GetBytes(sb.ToString());

        IDeserializerResolver realResolver = CreateRealJsonDeserializerResolver();

        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(
                    typeof(UntypedOrderAcceptConsumer),
                    typeof(UntypedOrder),
                    RoutingKeys: ["orders.*"],
                    AcceptUntyped: true),
            ],
            new Dictionary<Type, object>
            {
                [typeof(UntypedOrderAcceptConsumer)] = new UntypedOrderAcceptConsumer(log),
            },
            realResolver,
            logger);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new() { ["BW-RoutingKey"] = "orders.created" }, body: deepJson),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — deserialization must be rejected; consumer NOT invoked; dispatched == false.
        log.Should().BeEmpty("JSON exceeding MaxDepth must not reach the consumer");

        // The type-less deserialization failure logger must have fired (sanitised — only type name).
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Type-less deserialization rejected"),
            "a warning for the rejected deserialization must be present");

        // No consumer matched → rejection warning also fires.
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("No consumer matched"));

        // ADR-030 §Security (decision #4 / SEC-2): the JsonException message — which can embed the
        // foreign-JSON path/token — must NEVER leak into any log entry on the type-less catch path.
        // Only ex.GetType().Name is logged, so neither the leaf value nor the JSON structure appears.
        logger.Entries.Should().NotContain(e => e.Message.Contains("deep") || e.Message.Contains("{\"a\""),
            "the foreign-JSON payload fragment must never appear in any log entry");
    }

    // ── Test 7: no polymorphic $type dispatch (REAL deserializer required — GAP-2) ───────────────

    [Fact]
    public async Task DispatchMessageAsync_TypeLessDelivery_PolymorphicTypeDiscriminatorIgnored()
    {
        // Arrange — send a payload containing a "$type" discriminator that would cause type-confusion
        // if the deserializer honoured polymorphic dispatch. BareWireJsonSerializerOptions.Default
        // has no TypeInfoResolver, so "$type" is treated as an unknown property and the object is
        // deserialized as the DECLARED TMessage (UntypedOrder). We verify dispatched == true and the
        // consumer received a valid UntypedOrder (not a different type, not null), proving that
        // $type was ignored (regression lock, GAP-2 — no-polymorphic guarantee).
        List<string> log = [];

        // Payload: valid UntypedOrder JSON with a spurious "$type" field that STJ should ignore.
        byte[] payloadWithTypeDiscriminator = Encoding.UTF8.GetBytes(
            """{"$type":"SomeForeignType","orderId":"foreign-42"}""");

        IDeserializerResolver realResolver = CreateRealJsonDeserializerResolver();

        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(
                    typeof(UntypedOrderAcceptConsumer),
                    typeof(UntypedOrder),
                    RoutingKeys: ["orders.*"],
                    AcceptUntyped: true),
            ],
            new Dictionary<Type, object>
            {
                [typeof(UntypedOrderAcceptConsumer)] = new UntypedOrderAcceptConsumer(log),
            },
            realResolver,
            NullLogger<ReceiveEndpointRunner>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new() { ["BW-RoutingKey"] = "orders.created" }, body: payloadWithTypeDiscriminator),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — dispatched to the DECLARED UntypedOrder consumer; $type not honoured.
        // "foreign-42" proves the real payload was deserialized (not a mock return value),
        // and the consumer received UntypedOrder (no type-confusion).
        log.Should().ContainSingle().Which.Should().Be("AcceptUntyped:foreign-42",
            "the $type discriminator must be ignored; consumer receives the declared TMessage");
    }

    // ── Test 8: no AcceptUntyped candidate → fallthrough to layer 4 ──────────────────────────────

    [Fact]
    public async Task DispatchMessageAsync_TypeLessDelivery_NoAcceptUntypedCandidate_FallsThroughToLayer4()
    {
        // Arrange — consumer has a routing-key pattern but AcceptUntyped == false. A type-less
        // delivery produces no layer-3 candidate → dispatched == false → layer 4 (Reject).
        List<string> log = [];
        CapturingLogger logger = new();

        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(
                    typeof(UntypedOrderNoAcceptConsumer),
                    typeof(UntypedOrder),
                    RoutingKeys: ["orders.*"],
                    AcceptUntyped: false),
            ],
            new Dictionary<Type, object>
            {
                [typeof(UntypedOrderNoAcceptConsumer)] = new UntypedOrderNoAcceptConsumer(log),
            },
            CreateMockDeserializerResolver(new UntypedOrder("ord-1")),
            logger);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new() { ["BW-RoutingKey"] = "orders.created" }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — nothing dispatched; "No consumer matched" warning fires (layer 4 fallthrough).
        log.Should().BeEmpty();
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("No consumer matched"),
            "delivery must fall through to layer 4 when there is no AcceptUntyped candidate");
    }

    // ── Test 9: guard regression — legacy blind fallback unchanged when feature is inactive ───────

    [Fact]
    public async Task DispatchMessageAsync_FeatureInactive_TypeLess_LegacyBlindFallbackStillFires()
    {
        // Arrange — consumer has NO patterns and NO AcceptUntyped() → _hasAnyRoutingKeys == false
        // → layer 3 is never entered; the bit-identical pre-ADR-030 blind sequential-deserialize
        // path (SelectTypedLegacyAsync) handles the type-less delivery and dispatches to the
        // sole consumer via deserialization fallback.
        List<string> log = [];

        var (runner, writer) = CreateRunner(
            [
                // No RoutingKeys, no AcceptUntyped — purely legacy consumer.
                new ConsumerRegistration(
                    typeof(UntypedOrderAcceptConsumer),
                    typeof(UntypedOrder)),
            ],
            new Dictionary<Type, object>
            {
                [typeof(UntypedOrderAcceptConsumer)] = new UntypedOrderAcceptConsumer(log),
            },
            CreateMockDeserializerResolver(new UntypedOrder("ord-legacy")),
            NullLogger<ReceiveEndpointRunner>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        // No BW-MessageType, no BW-RoutingKey — purely type-less via the legacy path.
        await writer.WriteAsync(MakeMessage([]), cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — the legacy blind fallback dispatched the consumer; layer 3 was not involved.
        log.Should().ContainSingle().Which.Should().Be("AcceptUntyped:ord-legacy",
            "the pre-ADR-030 blind fallback must still fire when _hasAnyRoutingKeys == false");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="ReceiveEndpointRunner"/> wired to an in-memory channel.
    /// Adapted from <c>ReceiveEndpointRunnerRoutingKeyTests.CreateRunner</c> (private static,
    /// GAP-4 — must be copied/adapted into this class, not referenced directly).
    /// </summary>
    private static (ReceiveEndpointRunner Runner, ChannelWriter<InboundMessage> Writer) CreateRunner(
        IReadOnlyList<ConsumerRegistration> consumers,
        IReadOnlyDictionary<Type, object> instances,
        IDeserializerResolver deserializerResolver,
        ILogger logger)
    {
        Channel<InboundMessage> channel = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(64) { SingleWriter = false, SingleReader = true });

        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");
        adapter.ConsumeAsync(
                Arg.Any<string>(),
                Arg.Any<FlowControlOptions>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo => ReadChannelAsync(channel.Reader, callInfo.ArgAt<CancellationToken>(2)));
        adapter.SettleAsync(
                Arg.Any<SettlementAction>(),
                Arg.Any<InboundMessage>(),
                Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        foreach (KeyValuePair<Type, object> kv in instances)
        {
            provider.GetService(kv.Key).Returns(kv.Value);
        }

        provider.GetService(typeof(IEnumerable<IMessageMiddleware>))
            .Returns(Array.Empty<IMessageMiddleware>());

        FlowController flowController = new(NullLogger<FlowController>.Instance);

        EndpointBinding binding = new()
        {
            EndpointName = EndpointName,
            PrefetchCount = 4,
            Consumers = consumers,
            RawConsumers = [],
        };

        ReceiveEndpointRunner runner = new(
            binding,
            adapter,
            deserializerResolver,
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<ISendEndpointProvider>(),
            scopeFactory,
            flowController,
            new NullInstrumentation(),
            logger,
            loggerFactory: NullLoggerFactory.Instance);

        return (runner, channel.Writer);
    }

    /// <summary>
    /// Creates a mock <see cref="IDeserializerResolver"/> whose deserializer returns
    /// <paramref name="returnValue"/> for any deserialization call. Suitable for tests 1-5, 8, 9
    /// where the deserialization content is not the focus of the test.
    /// </summary>
    private static IDeserializerResolver CreateMockDeserializerResolver<TMessage>(TMessage returnValue)
        where TMessage : class
    {
        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        deserializer.ContentType.Returns("application/json");
        deserializer.Deserialize<TMessage>(Arg.Any<ReadOnlySequence<byte>>()).Returns(returnValue);

        IDeserializerResolver resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve(Arg.Any<string?>()).Returns(deserializer);
        return resolver;
    }

    /// <summary>
    /// Creates a <see cref="ContentTypeDeserializerRouter"/> wired to the REAL
    /// <see cref="SystemTextJsonRawDeserializer"/>. Required for tests 6 and 7 (GAP-2): a mock
    /// deserializer would make the MaxDepth / no-polymorphic regression locks vacuous.
    /// </summary>
    private static ContentTypeDeserializerRouter CreateRealJsonDeserializerResolver()
    {
        SystemTextJsonRawDeserializer realDeserializer = new();
        return new ContentTypeDeserializerRouter(realDeserializer);
    }

    private static InboundMessage MakeMessage(
        Dictionary<string, string> headers,
        string id = "msg-1",
        byte[]? body = null)
    {
        body ??= """{"orderId":"ord-1"}"""u8.ToArray();
        return new InboundMessage(
            messageId: id,
            headers: headers,
            body: new ReadOnlySequence<byte>(body),
            deliveryTag: 1UL);
    }

    private static async IAsyncEnumerable<InboundMessage> ReadChannelAsync(
        ChannelReader<InboundMessage> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (InboundMessage msg in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return msg;
        }
    }
}
