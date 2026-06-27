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
using BareWire.Configuration;
using BareWire.FlowControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

// ── Message types for MT envelope tests ─────────────────────────────────────
// Must be public so ConsumerInvokerFactory reflection can create generic methods over them.

public sealed record MtEnvelopeMessage(string Data);

public sealed record StandardMessage(string Data);

public sealed class MtEnvelopeConsumer(List<string> log) : IConsumer<MtEnvelopeMessage>
{
    public Task ConsumeAsync(ConsumeContext<MtEnvelopeMessage> context)
    {
        log.Add("MtEnvelope");
        return Task.CompletedTask;
    }
}

public sealed class StandardConsumer(List<string> log) : IConsumer<StandardMessage>
{
    public Task ConsumeAsync(ConsumeContext<StandardMessage> context)
    {
        log.Add("Standard");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Tests for per-consumer MassTransit envelope deserializer selection (task 18.5, D4 precedence).
/// Covers: forced MT despite content-type, default OFF, precedence over per-endpoint and global
/// router, format-mismatch settlement per dispatch path, mixed consumers, no-opt-in 0-B/op path,
/// and SEC-2 regression (AcceptUntyped warning not suppressed by UseMassTransitEnvelope).
/// </summary>
public sealed class ReceiveEndpointRunnerMassTransitEnvelopeTests
{
    private const string EndpointName = "mt-envelope-test-endpoint";

    // ── Test #1: forced MT despite content-type application/json ────────────────

    /// <summary>
    /// When a consumer is marked <c>UseMassTransitEnvelope = true</c>, the runner must use the MT
    /// deserializer regardless of the delivery's <c>content-type</c> header.
    /// </summary>
    [Fact]
    public async Task DispatchMessageAsync_MtMarkedConsumer_UsesMtDeserializerEvenWhenContentTypeIsJson()
    {
        // Arrange
        List<string> log = [];

        IMessageDeserializer mtDeserializer = Substitute.For<IMessageDeserializer>();
        mtDeserializer.ContentType.Returns("application/vnd.masstransit+json");
        // MT deserializer returns a valid message — so if MT path is taken, consumer fires.
        mtDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                      .Returns(new MtEnvelopeMessage("mt-data"));

        IMessageDeserializer defaultDeserializer = Substitute.For<IMessageDeserializer>();
        defaultDeserializer.ContentType.Returns("application/json");
        // Default deserializer returns null — if default path is taken, UnknownPayloadException
        // is thrown and consumer does NOT fire (test would then fail → RED before step 5).
        defaultDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                           .Returns((MtEnvelopeMessage?)null);

        IDeserializerResolver defaultResolver = Substitute.For<IDeserializerResolver>();
        defaultResolver.Resolve(Arg.Any<string?>()).Returns(defaultDeserializer);

        ConsumerRegistration registration = new(
            typeof(MtEnvelopeConsumer), typeof(MtEnvelopeMessage),
            UseMassTransitEnvelope: true);

        var (runner, writer, _) = CreateRunner(
            [registration],
            new Dictionary<Type, object> { [typeof(MtEnvelopeConsumer)] = new MtEnvelopeConsumer(log) },
            defaultResolver,
            mtDeserializer: mtDeserializer);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        // Delivery with content-type=application/json — but consumer is MT-marked → MT wins.
        await writer.WriteAsync(
            MakeMessage(new Dictionary<string, string>
            {
                ["BW-MessageType"] = nameof(MtEnvelopeMessage),
                ["content-type"] = "application/json",
            }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — consumer fired via the MT deserializer path.
        log.Should().ContainSingle().Which.Should().Be("MtEnvelope");
    }

    // ── Test #2: non-marked consumer keeps default deserializer (default OFF) ──

    /// <summary>
    /// A consumer without <c>UseMassTransitEnvelope</c> must keep using the per-endpoint or global
    /// deserializer resolver — the MT deserializer must not be applied (default OFF).
    /// </summary>
    [Fact]
    public async Task DispatchMessageAsync_NonMarkedConsumer_UsesDefaultDeserializerNotMt()
    {
        // Arrange
        List<string> log = [];

        IMessageDeserializer defaultDeserializer = Substitute.For<IMessageDeserializer>();
        defaultDeserializer.ContentType.Returns("application/json");
        defaultDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                           .Returns(new MtEnvelopeMessage("default-data"));

        IMessageDeserializer mtDeserializer = Substitute.For<IMessageDeserializer>();
        mtDeserializer.ContentType.Returns("application/vnd.masstransit+json");
        // MT should NOT be called for a non-marked consumer.
        mtDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                      .Returns((MtEnvelopeMessage?)null);

        IDeserializerResolver defaultResolver = Substitute.For<IDeserializerResolver>();
        defaultResolver.Resolve(Arg.Any<string?>()).Returns(defaultDeserializer);

        // Consumer NOT marked with UseMassTransitEnvelope (default = false).
        ConsumerRegistration registration = new(typeof(MtEnvelopeConsumer), typeof(MtEnvelopeMessage));

        var (runner, writer, _) = CreateRunner(
            [registration],
            new Dictionary<Type, object> { [typeof(MtEnvelopeConsumer)] = new MtEnvelopeConsumer(log) },
            defaultResolver,
            // mtDeserializer is still passed to simulate the runner being given one,
            // but it should NOT be used for a non-marked consumer.
            mtDeserializer: mtDeserializer);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new Dictionary<string, string> { ["BW-MessageType"] = nameof(MtEnvelopeMessage) }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — consumer fired; MT deserializer was never called for deserialization.
        log.Should().ContainSingle().Which.Should().Be("MtEnvelope");
        mtDeserializer.DidNotReceive().Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>());
    }

    // ── Test #3: per-consumer wins over per-endpoint override ───────────────────

    /// <summary>
    /// When the runner's <c>deserializerResolver</c> is already a <c>SingleDeserializerResolver</c>
    /// wrapping a per-endpoint override, a MT-marked consumer must still use the MT deserializer
    /// (per-consumer D4 precedence wins over per-endpoint).
    /// </summary>
    [Fact]
    public async Task DispatchMessageAsync_MtMarkedConsumer_WinsOverPerEndpointOverride()
    {
        // Arrange
        List<string> log = [];

        IMessageDeserializer mtDeserializer = Substitute.For<IMessageDeserializer>();
        mtDeserializer.ContentType.Returns("application/vnd.masstransit+json");
        mtDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                      .Returns(new MtEnvelopeMessage("mt-data"));

        // Per-endpoint override: a SingleDeserializerResolver wrapping a non-MT deserializer.
        // This simulates UseDeserializer<SomeDeserializer>() on the endpoint.
        IMessageDeserializer endpointOverrideDeserializer = Substitute.For<IMessageDeserializer>();
        endpointOverrideDeserializer.ContentType.Returns("application/json");
        // If the endpoint override is used instead of MT, Deserialize returns null → consumer doesn't fire.
        endpointOverrideDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                                    .Returns((MtEnvelopeMessage?)null);

        // The runner's deserializerResolver is the per-endpoint resolver (like BareWireBusControl
        // builds when DeserializerOverrideType != null).
        IDeserializerResolver endpointResolver = Substitute.For<IDeserializerResolver>();
        endpointResolver.Resolve(Arg.Any<string?>()).Returns(endpointOverrideDeserializer);

        ConsumerRegistration registration = new(
            typeof(MtEnvelopeConsumer), typeof(MtEnvelopeMessage),
            UseMassTransitEnvelope: true);

        var (runner, writer, _) = CreateRunner(
            [registration],
            new Dictionary<Type, object> { [typeof(MtEnvelopeConsumer)] = new MtEnvelopeConsumer(log) },
            endpointResolver,           // per-endpoint resolver — should be shadowed by MT for marked consumer
            mtDeserializer: mtDeserializer);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new Dictionary<string, string> { ["BW-MessageType"] = nameof(MtEnvelopeMessage) }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — per-consumer MT wins over per-endpoint override.
        log.Should().ContainSingle().Which.Should().Be("MtEnvelope");
        endpointOverrideDeserializer.DidNotReceive()
            .Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>());
    }

    // ── Test #4: per-consumer wins over global ContentTypeDeserializerRouter ──

    /// <summary>
    /// When the global resolver routes by content-type (simulating <c>ContentTypeDeserializerRouter</c>),
    /// a MT-marked consumer must deterministically use the MT deserializer — the routing-key
    /// content-type lookup on the delivery is bypassed for the marked consumer (D4 precedence).
    /// </summary>
    [Fact]
    public async Task DispatchMessageAsync_MtMarkedConsumer_WinsOverContentTypeDeserializerRouter()
    {
        // Arrange
        List<string> log = [];

        IMessageDeserializer mtDeserializer = Substitute.For<IMessageDeserializer>();
        mtDeserializer.ContentType.Returns("application/vnd.masstransit+json");
        mtDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                      .Returns(new MtEnvelopeMessage("mt-data"));

        IMessageDeserializer routerDefaultDeserializer = Substitute.For<IMessageDeserializer>();
        routerDefaultDeserializer.ContentType.Returns("application/json");
        // Router would return this for "application/json" content-type; if used → null → consumer misses.
        routerDefaultDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                                 .Returns((MtEnvelopeMessage?)null);

        // Global resolver simulates ContentTypeDeserializerRouter: routes "application/json" to the
        // default deserializer. The marked consumer must bypass this and use mtDeserializer directly.
        IDeserializerResolver globalRouter = Substitute.For<IDeserializerResolver>();
        globalRouter.Resolve("application/json").Returns(routerDefaultDeserializer);
        globalRouter.Resolve(Arg.Is<string?>(s => s != "application/json")).Returns(routerDefaultDeserializer);

        ConsumerRegistration registration = new(
            typeof(MtEnvelopeConsumer), typeof(MtEnvelopeMessage),
            UseMassTransitEnvelope: true);

        var (runner, writer, _) = CreateRunner(
            [registration],
            new Dictionary<Type, object> { [typeof(MtEnvelopeConsumer)] = new MtEnvelopeConsumer(log) },
            globalRouter,
            mtDeserializer: mtDeserializer);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        // Delivery with content-type=application/json; normally the router would route to the default
        // deserializer. The MT-marked consumer must deterministically use MT instead.
        await writer.WriteAsync(
            MakeMessage(new Dictionary<string, string>
            {
                ["BW-MessageType"] = nameof(MtEnvelopeMessage),
                ["content-type"] = "application/json",
            }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — per-consumer MT wins over the global content-type router.
        log.Should().ContainSingle().Which.Should().Be("MtEnvelope");
        routerDefaultDeserializer.DidNotReceive()
            .Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>());
    }

    // ── Test #5a: format mismatch → Nack (typed-header fast path) ──────────────

    /// <summary>
    /// When an MT-marked consumer receives a raw (non-envelope) delivery that carries a
    /// <c>BW-MessageType</c> header, the MT deserializer returns <see langword="null"/> →
    /// <c>UnknownPayloadException</c> propagates through the typed-header fast path (no local catch)
    /// → outer catch in <c>ProcessMessageAsync</c> → <b>Nack</b>.
    /// (DEC SEC-1 path: typed-header / pattern layers 1-2 → Nack, not Reject.)
    /// </summary>
    [Fact]
    public async Task DispatchMessageAsync_MtMarkedConsumer_RawDeliveryWithBwMessageTypeHeader_SettlesNack()
    {
        // Arrange — MT deserializer returns null (simulates raw / non-envelope payload).
        IMessageDeserializer mtDeserializer = Substitute.For<IMessageDeserializer>();
        mtDeserializer.ContentType.Returns("application/vnd.masstransit+json");
        mtDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                      .Returns((MtEnvelopeMessage?)null);

        IMessageDeserializer defaultDeserializer = Substitute.For<IMessageDeserializer>();
        defaultDeserializer.ContentType.Returns("application/json");
        IDeserializerResolver defaultResolver = Substitute.For<IDeserializerResolver>();
        defaultResolver.Resolve(Arg.Any<string?>()).Returns(defaultDeserializer);

        ConsumerRegistration registration = new(
            typeof(MtEnvelopeConsumer), typeof(MtEnvelopeMessage),
            UseMassTransitEnvelope: true);

        var (runner, writer, adapter) = CreateRunner(
            [registration],
            new Dictionary<Type, object>(), // consumer instance not needed — it never fires
            defaultResolver,
            mtDeserializer: mtDeserializer);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        // Delivery has BW-MessageType → typed-header FAST PATH (no local UnknownPayloadException catch)
        // → exception propagates to outer catch → Nack.
        await writer.WriteAsync(
            MakeMessage(new Dictionary<string, string> { ["BW-MessageType"] = nameof(MtEnvelopeMessage) }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — settlement is Nack (DEC SEC-1: typed-header fast path).
        await adapter.Received(1).SettleAsync(
            SettlementAction.Nack,
            Arg.Any<InboundMessage>(),
            Arg.Any<CancellationToken>());
        await adapter.DidNotReceive().SettleAsync(
            SettlementAction.Reject,
            Arg.Any<InboundMessage>(),
            Arg.Any<CancellationToken>());
    }

    // ── Test #5b: format mismatch → Reject (fallback/no-BW-MessageType path) ──

    /// <summary>
    /// When an MT-marked consumer receives a raw (non-envelope) delivery WITHOUT a
    /// <c>BW-MessageType</c> header, the MT deserializer returns <see langword="null"/> →
    /// <c>UnknownPayloadException</c> caught locally in the legacy fallback loop → consumer not
    /// dispatched → falls through to layer 4 → <b>Reject</b>.
    /// (DEC SEC-1 path: legacy fallback / untyped layer-3 → Reject, not Nack.)
    /// </summary>
    [Fact]
    public async Task DispatchMessageAsync_MtMarkedConsumer_RawDeliveryWithoutBwMessageTypeHeader_SettlesReject()
    {
        // Arrange — MT deserializer returns null on raw payload.
        IMessageDeserializer mtDeserializer = Substitute.For<IMessageDeserializer>();
        mtDeserializer.ContentType.Returns("application/vnd.masstransit+json");
        mtDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                      .Returns((MtEnvelopeMessage?)null);

        IMessageDeserializer defaultDeserializer = Substitute.For<IMessageDeserializer>();
        defaultDeserializer.ContentType.Returns("application/json");
        IDeserializerResolver defaultResolver = Substitute.For<IDeserializerResolver>();
        defaultResolver.Resolve(Arg.Any<string?>()).Returns(defaultDeserializer);

        ConsumerRegistration registration = new(
            typeof(MtEnvelopeConsumer), typeof(MtEnvelopeMessage),
            UseMassTransitEnvelope: true);

        var (runner, writer, adapter) = CreateRunner(
            [registration],
            new Dictionary<Type, object>(), // consumer never fires
            defaultResolver,
            mtDeserializer: mtDeserializer);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        // NO BW-MessageType header → legacy fallback loop.
        // UnknownPayloadException caught locally → falls through → Reject.
        await writer.WriteAsync(MakeMessage([]), cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — settlement is Reject (DEC SEC-1: legacy fallback path).
        await adapter.Received(1).SettleAsync(
            SettlementAction.Reject,
            Arg.Any<InboundMessage>(),
            Arg.Any<CancellationToken>());
        await adapter.DidNotReceive().SettleAsync(
            SettlementAction.Nack,
            Arg.Any<InboundMessage>(),
            Arg.Any<CancellationToken>());
    }

    // ── Test #6: mixed consumers on one endpoint ─────────────────────────────────

    /// <summary>
    /// When an endpoint has one MT-marked consumer and one unmarked consumer, each consumer must
    /// use its own designated deserializer — MT-marked uses the MT deserializer, unmarked uses the
    /// default resolver. The two consumers are isolated.
    /// </summary>
    [Fact]
    public async Task DispatchMessageAsync_MixedConsumers_EachUsesCorrectDeserializer()
    {
        // Arrange
        List<string> log = [];

        IMessageDeserializer mtDeserializer = Substitute.For<IMessageDeserializer>();
        mtDeserializer.ContentType.Returns("application/vnd.masstransit+json");
        mtDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                      .Returns(new MtEnvelopeMessage("mt-data"));

        IMessageDeserializer defaultDeserializer = Substitute.For<IMessageDeserializer>();
        defaultDeserializer.ContentType.Returns("application/json");
        // Default returns null for MtEnvelopeMessage (if used for the MT consumer → would fail)
        // and a valid StandardMessage for the standard consumer.
        defaultDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                           .Returns((MtEnvelopeMessage?)null);
        defaultDeserializer.Deserialize<StandardMessage>(Arg.Any<ReadOnlySequence<byte>>())
                           .Returns(new StandardMessage("standard-data"));

        IDeserializerResolver defaultResolver = Substitute.For<IDeserializerResolver>();
        defaultResolver.Resolve(Arg.Any<string?>()).Returns(defaultDeserializer);

        ConsumerRegistration mtRegistration = new(
            typeof(MtEnvelopeConsumer), typeof(MtEnvelopeMessage),
            UseMassTransitEnvelope: true);
        ConsumerRegistration stdRegistration = new(typeof(StandardConsumer), typeof(StandardMessage));

        var (runner, writer, _) = CreateRunner(
            [mtRegistration, stdRegistration],
            new Dictionary<Type, object>
            {
                [typeof(MtEnvelopeConsumer)] = new MtEnvelopeConsumer(log),
                [typeof(StandardConsumer)] = new StandardConsumer(log),
            },
            defaultResolver,
            mtDeserializer: mtDeserializer);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        // Message for the MT-marked consumer.
        await writer.WriteAsync(
            MakeMessage(new Dictionary<string, string>
            {
                ["BW-MessageType"] = nameof(MtEnvelopeMessage),
                ["content-type"] = "application/json",
            }, id: "msg-mt"),
            cts.Token);

        // Message for the unmarked standard consumer.
        await writer.WriteAsync(
            MakeMessage(new Dictionary<string, string>
            {
                ["BW-MessageType"] = nameof(StandardMessage),
                ["content-type"] = "application/json",
            }, id: "msg-std"),
            cts.Token);

        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — both consumers fired independently with their correct deserializer.
        log.Should().HaveCount(2);
        log.Should().Contain("MtEnvelope");
        log.Should().Contain("Standard");

        // MT deserializer was used ONLY for the MT-marked consumer.
        mtDeserializer.Received(1).Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>());
        mtDeserializer.DidNotReceive().Deserialize<StandardMessage>(Arg.Any<ReadOnlySequence<byte>>());

        // Default deserializer was used ONLY for the standard consumer.
        defaultDeserializer.DidNotReceive().Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>());
        defaultDeserializer.Received(1).Deserialize<StandardMessage>(Arg.Any<ReadOnlySequence<byte>>());
    }

    // ── Test #7: no-opt-in path (0-B/op boundary proxy) ─────────────────────────

    /// <summary>
    /// When no consumer has <c>UseMassTransitEnvelope = true</c>, <c>ResolverFor(i)</c> returns
    /// the shared <see cref="_deserializerResolver"/> reference unchanged — dispatch behavior is
    /// bit-identical to the pre-18.5 path (boundary proxy for the 0-B/op benchmark, task 18.8).
    /// </summary>
    [Fact]
    public async Task DispatchMessageAsync_NoMtMarkedConsumers_DefaultDeserializerUsedUnchanged()
    {
        // Arrange — no UseMassTransitEnvelope consumers; no MT deserializer passed.
        List<string> log = [];

        IMessageDeserializer defaultDeserializer = Substitute.For<IMessageDeserializer>();
        defaultDeserializer.ContentType.Returns("application/json");
        defaultDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                           .Returns(new MtEnvelopeMessage("default-data"));

        IDeserializerResolver defaultResolver = Substitute.For<IDeserializerResolver>();
        defaultResolver.Resolve(Arg.Any<string?>()).Returns(defaultDeserializer);

        ConsumerRegistration registration = new(typeof(MtEnvelopeConsumer), typeof(MtEnvelopeMessage));

        var (runner, writer, _) = CreateRunner(
            [registration],
            new Dictionary<Type, object> { [typeof(MtEnvelopeConsumer)] = new MtEnvelopeConsumer(log) },
            defaultResolver,
            mtDeserializer: null); // No MT deserializer — _hasAnyMtEnvelope is false.

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new Dictionary<string, string> { ["BW-MessageType"] = nameof(MtEnvelopeMessage) }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — consumer dispatched normally; default resolver used exclusively.
        log.Should().ContainSingle().Which.Should().Be("MtEnvelope");
        defaultResolver.Received().Resolve(Arg.Any<string?>());
    }

    // ── Test #8: SEC-2 regression — AcceptUntyped warning not suppressed ────────

    /// <summary>
    /// The existing <c>AcceptUntyped</c> trust-boundary advisory warning must still fire when
    /// <c>UseMassTransitEnvelope()</c> is ALSO set on the same consumer (no suppression by the
    /// envelope flag). Mirrors <c>UntypedTrustBoundaryDiagnosticTests</c> assertion pattern.
    /// </summary>
    [Fact]
    public void UntypedTrustBoundaryDiagnostic_WhenBothAcceptUntypedAndUseMtEnvelopeSet_StillEmitsWarning()
    {
        // Arrange
        FakeLogger logger = new();
        BusConfigurator configurator = new();
        configurator.AddReceiveEndpoint(
            "mt-untyped-queue",
            ep => ep.Consumer<MtEnvelopeConsumer, MtEnvelopeMessage>(c =>
            {
                c.RoutingKey("orders.*");
                c.AcceptUntyped();
                c.UseMassTransitEnvelope(); // combined with AcceptUntyped — warning must not be suppressed
            }));

        // Act
        UntypedTrustBoundaryDiagnostic.Run(configurator, logger);

        // Assert — both advisories fire (DEC-1, task 18.7 additive): the type-less AcceptUntyped()
        // advisory is NOT suppressed by UseMassTransitEnvelope(), AND the per-consumer MT-envelope
        // axis adds its own advisory. Both name the endpoint; neither suppresses the other.
        var warnings = logger.Events.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        warnings.Should().Contain(m => m.Contains("mt-untyped-queue") && m.Contains("declares AcceptUntyped()"));
        warnings.Should().Contain(m => m.Contains("mt-untyped-queue") && m.Contains("MassTransit envelope"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="ReceiveEndpointRunner"/> backed by an in-memory channel.
    /// Returns the runner, the channel writer for pumping messages, and the mock adapter
    /// for asserting settlement actions.
    /// </summary>
    private static (ReceiveEndpointRunner Runner, ChannelWriter<InboundMessage> Writer, ITransportAdapter Adapter)
        CreateRunner(
            IReadOnlyList<ConsumerRegistration> consumers,
            IReadOnlyDictionary<Type, object> instances,
            IDeserializerResolver deserializerResolver,
            IMessageDeserializer? mtDeserializer = null)
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
            NullLogger<ReceiveEndpointRunner>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            massTransitEnvelopeDeserializer: mtDeserializer);

        return (runner, channel.Writer, adapter);
    }

    private static InboundMessage MakeMessage(
        Dictionary<string, string> headers,
        string id = "msg-1")
    {
        byte[] body = """{"Data":"test"}"""u8.ToArray();
        return new InboundMessage(
            messageId: id,
            headers: headers,
            body: new ReadOnlySequence<byte>(body),
            deliveryTag: 1UL);
    }

    private static async IAsyncEnumerable<InboundMessage> ReadChannelAsync(
        ChannelReader<InboundMessage> reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (InboundMessage msg in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return msg;
        }
    }

    /// <summary>Minimal logger that captures emitted log events for assertion.</summary>
    private sealed class FakeLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Events.Add((logLevel, formatter(state, exception)));
    }
}
