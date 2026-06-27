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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

// Reuses the PUBLIC message/consumer types declared in ReceiveEndpointRunnerMassTransitEnvelopeTests.cs
// (same namespace): MtEnvelopeMessage, StandardMessage, MtEnvelopeConsumer, StandardConsumer. They must
// stay public so ConsumerInvokerFactory reflection can build generic methods over them.

/// <summary>
/// High-risk behavioral regression suite for per-consumer MassTransit envelope opt-in (task 18.9),
/// the feature-18 analogue of the 17.13 regression set. It locks the four named behaviors — mixed
/// consumers, precedence (per-consumer &gt; per-endpoint &gt; global), format-mismatch fail-fast, and
/// default OFF — driven through the <b>public configurator → dispatch</b> path: each test materializes
/// a <see cref="ConsumerRegistration"/> via <see cref="ReceiveEndpointConfiguration"/>
/// (<c>cfg.Consumer&lt;C,M&gt;(c =&gt; c.UseMassTransitEnvelope())</c>) and feeds the materialized list
/// into a real <see cref="ReceiveEndpointRunner"/>.
/// <para>
/// <b>Complementarity (deliberate non-duplication).</b> This file is the integration layer between two
/// existing seams: <c>ConsumerConfiguratorTests</c> (task 18.3) asserts materialization only and stops
/// before dispatch; <c>ReceiveEndpointRunnerMassTransitEnvelopeTests</c> (task 18.5) hand-builds
/// <c>new ConsumerRegistration(..., UseMassTransitEnvelope: true)</c> and drives the runner, bypassing the
/// configurator. The load-bearing difference here is the <i>source</i> of the registration: it flows
/// through <c>ReceiveEndpointConfiguration.Consumer&lt;,&gt;</c> / <c>ConsumerConfigurator.Build()</c>, so a
/// regression in the materialization → dispatch wiring (which passes both 18.3 and 18.5 individually) is
/// caught here. Tests #1 and #3 are therefore <i>wiring</i> tests of behaviors already covered behaviorally
/// by 18.5; tests #2 (three precedence axes in one scenario) and #4 (record value-equality of the default
/// shape) close genuine gaps 18.5 leaves open.
/// </para>
/// <para>
/// <b>Out of scope.</b> The secure-by-default startup advisory for combining
/// <c>UseMassTransitEnvelope()</c> with <c>AcceptUntyped()</c> without a schema-validation middleware is
/// owned and tested by task 18.7 (<c>UntypedTrustBoundaryDiagnostic</c> + its tests), a Build-time
/// diagnostic seam — not exercised here.
/// </para>
/// </summary>
public sealed class PerConsumerMtEnvelopeBehavioralRegressionTests
{
    private const string EndpointName = "per-consumer-mt-behavioral-endpoint";

    // ── Behavior 1: mixed consumers materialized from the public configurator ──────

    /// <summary>
    /// Wiring test (config → dispatch): an endpoint configured via the public configurator with one
    /// <c>UseMassTransitEnvelope()</c> consumer and one plain consumer must dispatch each through its own
    /// deserializer — the MT-marked consumer via the MT deserializer, the unmarked one via the default
    /// resolver. Unlike the hand-built-registration equivalent in task 18.5, the registrations here flow
    /// through <see cref="ReceiveEndpointConfiguration"/>.
    /// </summary>
    [Fact]
    public async Task DispatchMessageAsync_MixedConsumersMaterializedFromConfigurator_EachUsesItsDeserializer()
    {
        // Arrange — materialize BOTH registrations through the public configurator (not hand-built).
        List<string> log = [];

        ReceiveEndpointConfiguration cfg = new("mixed-queue");
        cfg.Consumer<MtEnvelopeConsumer, MtEnvelopeMessage>(c => c.UseMassTransitEnvelope());
        cfg.Consumer<StandardConsumer, StandardMessage>();

        IMessageDeserializer mtDeserializer = Substitute.For<IMessageDeserializer>();
        mtDeserializer.ContentType.Returns("application/vnd.masstransit+json");
        mtDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                      .Returns(new MtEnvelopeMessage("mt-data"));

        IMessageDeserializer defaultDeserializer = Substitute.For<IMessageDeserializer>();
        defaultDeserializer.ContentType.Returns("application/json");
        // Default returns null for the MT message (would miss if wrongly used) and a valid standard message.
        defaultDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                           .Returns((MtEnvelopeMessage?)null);
        defaultDeserializer.Deserialize<StandardMessage>(Arg.Any<ReadOnlySequence<byte>>())
                           .Returns(new StandardMessage("standard-data"));

        IDeserializerResolver defaultResolver = Substitute.For<IDeserializerResolver>();
        defaultResolver.Resolve(Arg.Any<string?>()).Returns(defaultDeserializer);

        var (runner, writer, _) = CreateRunner(
            cfg.ConsumerRegistrations,
            new Dictionary<Type, object>
            {
                [typeof(MtEnvelopeConsumer)] = new MtEnvelopeConsumer(log),
                [typeof(StandardConsumer)] = new StandardConsumer(log),
            },
            defaultResolver,
            mtDeserializer: mtDeserializer);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new Dictionary<string, string>
            {
                ["BW-MessageType"] = nameof(MtEnvelopeMessage),
                ["content-type"] = "application/json",
            }, id: "msg-mt"),
            cts.Token);
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

        // Assert — both fired independently; each deserializer used for exactly its own consumer.
        log.Should().HaveCount(2);
        log.Should().Contain("MtEnvelope");
        log.Should().Contain("Standard");

        mtDeserializer.Received(1).Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>());
        mtDeserializer.DidNotReceive().Deserialize<StandardMessage>(Arg.Any<ReadOnlySequence<byte>>());
        defaultDeserializer.Received(1).Deserialize<StandardMessage>(Arg.Any<ReadOnlySequence<byte>>());
        defaultDeserializer.DidNotReceive().Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>());
    }

    // ── Behavior 2: precedence — three axes in ONE scenario ────────────────────────

    /// <summary>
    /// Precedence (per-consumer &gt; per-endpoint &gt; global) proven in a single scenario: a consumer
    /// materialized with <c>UseMassTransitEnvelope()</c> runs on a resolver that simultaneously stands in
    /// for a per-endpoint override AND a content-type router (it returns the same non-MT deserializer for
    /// every content-type). The MT-marked consumer must deterministically use the MT deserializer, bypassing
    /// both lower axes. Task 18.5 covers these axes separately (#3 per-endpoint, #4 global) on hand-built
    /// registrations; this test closes the combined-in-one-scenario gap via the configurator.
    /// </summary>
    [Fact]
    public async Task DispatchMessageAsync_MarkedConsumerFromConfigurator_WinsOverPerEndpointOverrideAndGlobalRoute()
    {
        // Arrange
        List<string> log = [];

        ReceiveEndpointConfiguration cfg = new("precedence-queue");
        cfg.Consumer<MtEnvelopeConsumer, MtEnvelopeMessage>(c => c.UseMassTransitEnvelope());

        IMessageDeserializer mtDeserializer = Substitute.For<IMessageDeserializer>();
        mtDeserializer.ContentType.Returns("application/vnd.masstransit+json");
        mtDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                      .Returns(new MtEnvelopeMessage("mt-data"));

        // Stands in for BOTH a per-endpoint override and a global content-type router: same non-MT
        // deserializer for every content-type lookup. If used for the marked consumer → null → consumer misses.
        IMessageDeserializer lowerAxisDeserializer = Substitute.For<IMessageDeserializer>();
        lowerAxisDeserializer.ContentType.Returns("application/json");
        lowerAxisDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                             .Returns((MtEnvelopeMessage?)null);

        IDeserializerResolver lowerAxisResolver = Substitute.For<IDeserializerResolver>();
        lowerAxisResolver.Resolve(Arg.Any<string?>()).Returns(lowerAxisDeserializer);

        var (runner, writer, _) = CreateRunner(
            cfg.ConsumerRegistrations,
            new Dictionary<Type, object> { [typeof(MtEnvelopeConsumer)] = new MtEnvelopeConsumer(log) },
            lowerAxisResolver,          // per-endpoint override + global router — both shadowed by per-consumer MT
            mtDeserializer: mtDeserializer);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
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

        // Assert — per-consumer MT wins over BOTH lower axes; neither lower-axis deserialize was invoked.
        log.Should().ContainSingle().Which.Should().Be("MtEnvelope");
        lowerAxisDeserializer.DidNotReceive().Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>());
        mtDeserializer.Received(1).Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>());
    }

    // ── Behavior 3: format mismatch → deterministic fail-fast, no silent dispatch ──

    /// <summary>
    /// Wiring test (config → dispatch): an MT-marked consumer (materialized via the configurator) that
    /// receives a raw / non-envelope delivery carrying a <c>BW-MessageType</c> header must fail fast and
    /// never silently dispatch a wrong payload. The MT deserializer returns <see langword="null"/> →
    /// <c>UnknownPayloadException</c> propagates through the typed-header fast path → outer catch → <b>Nack</b>;
    /// the consumer is never invoked (empty log). The complementary Reject (legacy-fallback) settlement path
    /// is covered at the runner seam by task 18.5 (#5a Nack / #5b Reject).
    /// </summary>
    [Fact]
    public async Task DispatchMessageAsync_MarkedConsumerFromConfigurator_RawDelivery_FailsFastWithoutSilentDispatch()
    {
        // Arrange — MT deserializer returns null (raw, non-envelope payload).
        List<string> log = [];

        ReceiveEndpointConfiguration cfg = new("mismatch-queue");
        cfg.Consumer<MtEnvelopeConsumer, MtEnvelopeMessage>(c => c.UseMassTransitEnvelope());

        IMessageDeserializer mtDeserializer = Substitute.For<IMessageDeserializer>();
        mtDeserializer.ContentType.Returns("application/vnd.masstransit+json");
        mtDeserializer.Deserialize<MtEnvelopeMessage>(Arg.Any<ReadOnlySequence<byte>>())
                      .Returns((MtEnvelopeMessage?)null);

        IMessageDeserializer defaultDeserializer = Substitute.For<IMessageDeserializer>();
        defaultDeserializer.ContentType.Returns("application/json");
        IDeserializerResolver defaultResolver = Substitute.For<IDeserializerResolver>();
        defaultResolver.Resolve(Arg.Any<string?>()).Returns(defaultDeserializer);

        var (runner, writer, adapter) = CreateRunner(
            cfg.ConsumerRegistrations,
            // Instance present but must NEVER fire — proves no silent dispatch on a null deserialize.
            new Dictionary<Type, object> { [typeof(MtEnvelopeConsumer)] = new MtEnvelopeConsumer(log) },
            defaultResolver,
            mtDeserializer: mtDeserializer);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        // BW-MessageType header → typed-header fast path → exception propagates → Nack.
        await writer.WriteAsync(
            MakeMessage(new Dictionary<string, string> { ["BW-MessageType"] = nameof(MtEnvelopeMessage) }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — deterministic fail-fast: consumer never fired, settlement is Nack (not Reject).
        log.Should().BeEmpty();
        await adapter.Received(1).SettleAsync(
            SettlementAction.Nack, Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>());
        await adapter.DidNotReceive().SettleAsync(
            SettlementAction.Reject, Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>());
    }

    // ── Behavior 4: default OFF — bit-identity (record value-equality + dispatch) ──

    /// <summary>
    /// Default OFF, two complementary aspects under one behavior. (A) A consumer materialized WITHOUT
    /// <c>UseMassTransitEnvelope()</c> produces a <see cref="ConsumerRegistration"/> that is record-equal to
    /// the pre-feature default shape (<c>new ConsumerRegistration(consumerType, messageType)</c>) — zero
    /// behavioral drift. (B) When no consumer opts in, the runner dispatches through the unchanged default
    /// resolver (the <c>_hasAnyMtEnvelope == false</c> short-circuit path). This is a <b>behavioral proxy</b>
    /// for the bit-identical path: it asserts value-equality and an unchanged resolver, NOT an allocation
    /// count — the 0 B/op proof belongs to benchmark 18.8.
    /// </summary>
    [Fact]
    public async Task Materialization_ConsumerWithoutOptIn_IsRecordEqualToPreFeatureDefaultAndDispatchesUnchanged()
    {
        // Arrange (A) — materialize via the public configurator without any opt-in.
        ReceiveEndpointConfiguration cfg = new("default-off-queue");
        cfg.Consumer<StandardConsumer, StandardMessage>();

        ConsumerRegistration actual = cfg.ConsumerRegistrations.Should().ContainSingle().Subject;
        ConsumerRegistration expected = new(typeof(StandardConsumer), typeof(StandardMessage));

        // Assert (A) — record value-equality with the pre-feature default shape (flags all default).
        actual.Should().Be(expected);
        actual.UseMassTransitEnvelope.Should().BeFalse();
        actual.AcceptUntyped.Should().BeFalse();
        actual.RoutingKeys.Should().BeNull();

        // Arrange (B) — dispatch with no MT deserializer (_hasAnyMtEnvelope == false → unchanged path).
        List<string> log = [];

        IMessageDeserializer defaultDeserializer = Substitute.For<IMessageDeserializer>();
        defaultDeserializer.ContentType.Returns("application/json");
        defaultDeserializer.Deserialize<StandardMessage>(Arg.Any<ReadOnlySequence<byte>>())
                           .Returns(new StandardMessage("standard-data"));

        IDeserializerResolver defaultResolver = Substitute.For<IDeserializerResolver>();
        defaultResolver.Resolve(Arg.Any<string?>()).Returns(defaultDeserializer);

        var (runner, writer, _) = CreateRunner(
            cfg.ConsumerRegistrations,
            new Dictionary<Type, object> { [typeof(StandardConsumer)] = new StandardConsumer(log) },
            defaultResolver,
            mtDeserializer: null); // No MT deserializer — bit-identical pre-feature dispatch.

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new Dictionary<string, string> { ["BW-MessageType"] = nameof(StandardMessage) }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert (B) — dispatched normally through the unchanged default resolver.
        log.Should().ContainSingle().Which.Should().Be("Standard");
        defaultResolver.Received().Resolve(Arg.Any<string?>());
    }

    // ── Helpers (private; mirror ReceiveEndpointRunnerMassTransitEnvelopeTests) ─────

    /// <summary>
    /// Creates a <see cref="ReceiveEndpointRunner"/> backed by an in-memory channel, fed the supplied
    /// (configurator-materialized) registrations. Returns the runner, the channel writer for pumping
    /// messages, and the mock adapter for asserting settlement actions.
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
}
