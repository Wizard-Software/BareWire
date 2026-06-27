using System.Buffers;
using System.Runtime.CompilerServices;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

// ── Message types for high-risk behavioral tests (17.13) ────────────────────────────────────────────
// Two DISTINCT TMessage types are required to exercise the type-less collision scenario: two
// AcceptUntyped() consumers of DIFFERENT declared types whose patterns overlap on one routing key.
// Declared public so ConsumerInvokerFactory resolves them generically at startup. Names are unique in
// this namespace (no collision with UntypedOrder / UntypedEvent / TransferInitiated).

public sealed record RiskAlpha(string Id);

public sealed record RiskBeta(string Id);

public sealed class RiskAlphaConsumer(List<string> log) : IConsumer<RiskAlpha>
{
    public Task ConsumeAsync(ConsumeContext<RiskAlpha> context)
    {
        log.Add("Alpha");
        return Task.CompletedTask;
    }
}

public sealed class RiskBetaConsumer(List<string> log) : IConsumer<RiskBeta>
{
    public Task ConsumeAsync(ConsumeContext<RiskBeta> context)
    {
        log.Add("Beta");
        return Task.CompletedTask;
    }
}

/// <summary>
/// High-risk behavioral regression-locks for the consume-time routing-key dispatch (ADR-030 §Enforcement),
/// implemented in 17.8 (layers 1-2 + guard) and 17.9 (layer 3 type-less). These tests lock the three
/// highest-risk behaviors that the per-type suites (<see cref="ReceiveEndpointRunnerRoutingKeyTests"/>,
/// <see cref="ReceiveEndpointRunnerUntypedDispatchTests"/>) do not assert explicitly:
/// <list type="number">
///   <item><description><b>Type-less collision (disjointness):</b> two <c>AcceptUntyped()</c> consumers of
///   DIFFERENT <c>TMessage</c> sharing a routing key resolve deterministically — most-specific-wins, or
///   first-registered + warning on an unresolvable tie (T1/T2).</description></item>
///   <item><description><b>Opt-in dichotomy:</b> a consumer's routing-key pattern WITHOUT
///   <c>AcceptUntyped()</c> narrows ONLY typed dispatch — it never makes the consumer a type-less sink
///   (T3) yet stays live for typed deliveries (T4).</description></item>
///   <item><description><b>Bit-identity of the legacy path:</b> an endpoint with no patterns and no
///   <c>AcceptUntyped()</c> takes the pre-ADR-030 path — header fast-path break-on-first (T5) and blind
///   sequential-deserialize fallback break-on-first (T6); layer 3 is never activated.</description></item>
/// </list>
/// The harness mirrors <see cref="ReceiveEndpointRunnerUntypedDispatchTests"/> (private static helpers are
/// per-class by convention, GAP-4); the shared <see cref="CapturingLogger"/> and <c>NullInstrumentation</c>
/// are reused. Each test's failsafe (the regression it would catch) is documented in 17.13-plan.md §4.
/// </summary>
public sealed class ReceiveEndpointRunnerHighRiskBehaviorTests
{
    private const string EndpointName = "high-risk-behavior-test-endpoint";

    // ── Scenario 1: type-less collision (disjointness) — different TMessage types ─────────────────────

    [Fact]
    public async Task DispatchMessageAsync_TypeLessCollisionDifferentTMessage_MostSpecificWinsAcrossTypes()
    {
        // Arrange — two AcceptUntyped consumers of DIFFERENT types: RiskBeta with the wider "risk.#" and
        // RiskAlpha with the more specific "risk.eu.*". A type-less delivery (no BW-MessageType) whose key
        // matches BOTH must resolve to the most specific pattern's consumer regardless of declared type.
        List<string> log = [];
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(
                    typeof(RiskBetaConsumer), typeof(RiskBeta), RoutingKeys: ["risk.#"], AcceptUntyped: true),
                new ConsumerRegistration(
                    typeof(RiskAlphaConsumer), typeof(RiskAlpha), RoutingKeys: ["risk.eu.*"], AcceptUntyped: true),
            ],
            new Dictionary<Type, object>
            {
                [typeof(RiskBetaConsumer)] = new RiskBetaConsumer(log),
                [typeof(RiskAlphaConsumer)] = new RiskAlphaConsumer(log),
            },
            CreateMockDeserializerResolver(new RiskAlpha("a-1")),
            NullLogger<ReceiveEndpointRunner>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new() { ["BW-RoutingKey"] = "risk.eu.created" }), // no BW-MessageType
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — only the more specific RiskAlpha consumer fires; the wider RiskBeta does not.
        log.Should().ContainSingle().Which.Should().Be("Alpha");
    }

    [Fact]
    public async Task DispatchMessageAsync_TypeLessCollisionDifferentTMessage_UnresolvableTie_FirstRegisteredAndWarnsAndNoKeyLeak()
    {
        // Arrange — two AcceptUntyped consumers of DIFFERENT types with the SAME pattern → identical
        // specificity → unresolvable tie. The first-registered (RiskAlpha) must win, a warning must fire,
        // and the raw routing key must never appear in any log entry (ADR-030 §Bezpieczeństwo).
        const string tieKey = "risk.placed";
        List<string> log = [];
        CapturingLogger logger = new();
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(
                    typeof(RiskAlphaConsumer), typeof(RiskAlpha), RoutingKeys: ["risk.*"], AcceptUntyped: true),
                new ConsumerRegistration(
                    typeof(RiskBetaConsumer), typeof(RiskBeta), RoutingKeys: ["risk.*"], AcceptUntyped: true),
            ],
            new Dictionary<Type, object>
            {
                [typeof(RiskAlphaConsumer)] = new RiskAlphaConsumer(log),
                [typeof(RiskBetaConsumer)] = new RiskBetaConsumer(log),
            },
            CreateMockDeserializerResolver(new RiskAlpha("a-1")),
            logger);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(MakeMessage(new() { ["BW-RoutingKey"] = tieKey }), cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — first-registered RiskAlpha wins; ambiguity warning fired; raw key never logged.
        log.Should().ContainSingle().Which.Should().Be("Alpha");
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Ambiguous routing-key match"));
        logger.Entries.Should().NotContain(e => e.Message.Contains(tieKey),
            "the raw routing-key value must never appear in any log message (ADR-030 §Security)");
    }

    // ── Scenario 2: opt-in dichotomy — pattern narrows typed dispatch ONLY ────────────────────────────

    [Fact]
    public async Task DispatchMessageAsync_TypeLessDelivery_PatternConsumerWithoutAcceptUntyped_NotSelected()
    {
        // Arrange — a consumer with a matching pattern but AcceptUntyped == false. A type-less delivery
        // must NOT reach it (secure-by-default gate, ADR-FIX-1) — it falls through to layer 4.
        List<string> log = [];
        CapturingLogger logger = new();
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(
                    typeof(RiskAlphaConsumer), typeof(RiskAlpha), RoutingKeys: ["risk.*"], AcceptUntyped: false),
            ],
            new Dictionary<Type, object>
            {
                [typeof(RiskAlphaConsumer)] = new RiskAlphaConsumer(log),
            },
            CreateMockDeserializerResolver(new RiskAlpha("a-1")),
            logger);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new() { ["BW-RoutingKey"] = "risk.created" }), // type-less delivery
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — consumer not selected; delivery falls through to layer 4 ("No consumer matched").
        log.Should().BeEmpty("a pattern consumer without AcceptUntyped() must never catch a type-less delivery");
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("No consumer matched"));
    }

    [Fact]
    public async Task DispatchMessageAsync_TypedDelivery_SamePatternConsumerWithoutAcceptUntyped_IsSelected()
    {
        // Arrange — the SAME registration as the opt-in-negative test (pattern "risk.*", AcceptUntyped:
        // false). A TYPED delivery (BW-MessageType present) with a matching key MUST be selected via
        // layer 1 — proving the pattern is live for typed dispatch, narrowing it ONLY (dichotomy).
        List<string> log = [];
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(
                    typeof(RiskAlphaConsumer), typeof(RiskAlpha), RoutingKeys: ["risk.*"], AcceptUntyped: false),
            ],
            new Dictionary<Type, object>
            {
                [typeof(RiskAlphaConsumer)] = new RiskAlphaConsumer(log),
            },
            CreateMockDeserializerResolver(new RiskAlpha("a-1")),
            NullLogger<ReceiveEndpointRunner>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new()
            {
                ["BW-MessageType"] = nameof(RiskAlpha),
                ["BW-RoutingKey"] = "risk.created",
            }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — the consumer IS selected on the typed path (layer 1).
        log.Should().ContainSingle().Which.Should().Be("Alpha",
            "the routing-key pattern must remain live for typed dispatch even without AcceptUntyped()");
    }

    // ── Scenario 3: bit-identity of the pre-ADR-030 legacy path ───────────────────────────────────────

    [Fact]
    public async Task DispatchMessageAsync_LegacyEndpoint_HeaderFastPath_BreakOnFirstAndNoBlindFallbackWhenHeaderPresent()
    {
        // Arrange — two legacy consumers of different types, NO patterns and NO AcceptUntyped() →
        // _hasAnyRoutingKeys == false → SelectTypedLegacyAsync. The header fast-path routes by type name
        // (break-on-first); a non-matching BW-MessageType selects nothing (no blind fallback when a header
        // is present). Both deliveries run in one consume loop.
        List<string> log = [];
        CapturingLogger logger = new();
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(typeof(RiskAlphaConsumer), typeof(RiskAlpha)),
                new ConsumerRegistration(typeof(RiskBetaConsumer), typeof(RiskBeta)),
            ],
            new Dictionary<Type, object>
            {
                [typeof(RiskAlphaConsumer)] = new RiskAlphaConsumer(log),
                [typeof(RiskBetaConsumer)] = new RiskBetaConsumer(log),
            },
            CreateMockDeserializerResolver(new RiskBeta("b-1")),
            logger);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        // (a) Header names RiskBeta → routes directly to RiskBeta by type name (RiskAlpha untouched).
        await writer.WriteAsync(MakeMessage(new() { ["BW-MessageType"] = nameof(RiskBeta) }), cts.Token);
        // (b) Header names a type with no registered consumer → nothing matches; no blind fallback fires.
        await writer.WriteAsync(MakeMessage(new() { ["BW-MessageType"] = "Nonexistent" }, id: "msg-2"), cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — only RiskBeta fired (delivery a); delivery b matched nothing (no blind fallback).
        log.Should().ContainSingle().Which.Should().Be("Beta",
            "the header fast-path must route by type name with break-on-first and no blind fallback when a header is present");
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("No consumer matched"),
            "a header naming an unregistered type must not blind-deserialize — it falls through to 'No consumer matched'");
    }

    [Fact]
    public async Task DispatchMessageAsync_LegacyEndpoint_TypeLess_BlindFallbackBreakOnFirstAndLayer3NotActivated()
    {
        // Arrange — two legacy consumers (RiskAlpha first, RiskBeta second), NO patterns and NO
        // AcceptUntyped() → _hasAnyRoutingKeys == false. A type-less delivery (no BW-MessageType) takes the
        // bit-identical blind sequential-deserialize fallback: the first invoker that deserializes
        // successfully wins (break-on-first). Layer 3 (SelectUntypedAsync) is never reached.
        List<string> log = [];
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(typeof(RiskAlphaConsumer), typeof(RiskAlpha)),
                new ConsumerRegistration(typeof(RiskBetaConsumer), typeof(RiskBeta)),
            ],
            new Dictionary<Type, object>
            {
                [typeof(RiskAlphaConsumer)] = new RiskAlphaConsumer(log),
                [typeof(RiskBetaConsumer)] = new RiskBetaConsumer(log),
            },
            CreateMockDeserializerResolver(new RiskAlpha("a-legacy")),
            NullLogger<ReceiveEndpointRunner>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        // No BW-MessageType and no BW-RoutingKey — purely type-less via the legacy blind fallback.
        await writer.WriteAsync(MakeMessage([]), cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — the first-registered consumer's deserialize succeeds and wins (break-on-first);
        // the second is never tried, and layer 3 was never activated (no AcceptUntyped consumer).
        log.Should().ContainSingle().Which.Should().Be("Alpha",
            "the legacy blind fallback must select the first successfully-deserializing consumer (break-on-first)");
    }

    // ── Helpers (copied/adapted per-class from ReceiveEndpointRunnerUntypedDispatchTests, GAP-4) ───────

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

    private static InboundMessage MakeMessage(
        Dictionary<string, string> headers,
        string id = "msg-1",
        byte[]? body = null)
    {
        body ??= """{"id":"x-1"}"""u8.ToArray();
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
