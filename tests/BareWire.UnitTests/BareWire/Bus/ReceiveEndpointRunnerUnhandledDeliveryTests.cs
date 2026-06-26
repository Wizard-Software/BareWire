using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Observability;
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

// ── Message types for unhandled-delivery tests ────────────────────────────────────────────────────
// Declared as public records so ConsumerInvokerFactory can resolve them generically at startup.

public sealed record UnhandledOrder(string OrderId);

// ── Consumer types ────────────────────────────────────────────────────────────────────────────────

public sealed class UnhandledOrderEuConsumer(List<string> log) : IConsumer<UnhandledOrder>
{
    public Task ConsumeAsync(ConsumeContext<UnhandledOrder> context)
    {
        log.Add($"Eu:{context.Message.OrderId}");
        return Task.CompletedTask;
    }
}

public sealed class UnhandledOrderCatchAllConsumer(List<string> log) : IConsumer<UnhandledOrder>
{
    public Task ConsumeAsync(ConsumeContext<UnhandledOrder> context)
    {
        log.Add($"CatchAll:{context.Message.OrderId}");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Inbox-filter middleware stub: marks the delivery as deduplicated (inbox-filtered) without
/// dispatching to any consumer. Used by test 4 to verify the dedup path is not misreported
/// as an unhandled delivery (ADR-030 D4 layer-4 guard).
/// </summary>
internal sealed class InboxFilterMiddleware : IMessageMiddleware
{
    public Task InvokeAsync(MessageContext context, NextMiddleware _)
    {
        context.Items[WellKnownItemKeys.InboxFiltered] = true;
        return Task.CompletedTask; // short-circuit: do not call nextMiddleware
    }
}

/// <summary>
/// Tests for ADR-030 D4 layer 4: a silently-unhandled delivery (no consumer matched,
/// <c>dispatched == false</c>) must be recorded as an observability metric via
/// <see cref="IBareWireInstrumentation.RecordFailure"/> with <c>error_type = "UnhandledDelivery"</c>.
/// Also verifies the routing-key-in-logs decision: raw BW-RoutingKey never leaks to logs or metric tags.
/// </summary>
public sealed class ReceiveEndpointRunnerUnhandledDeliveryTests
{
    private const string EndpointName = "unhandled-delivery-test-endpoint";

    // ── Test 1: metric recorded on unhandled delivery ───────────────────────────────────────────

    [Fact]
    public async Task ProcessMessageAsync_UnhandledDelivery_RecordsUnhandledDeliveryMetricAndRejects()
    {
        // Arrange — one consumer with routing key "orders.eu.*"; delivery BW-RoutingKey = "orders.us.created"
        // (does not match the pattern, no catch-all) → unhandled delivery.
        IBareWireInstrumentation instrumentation = Substitute.For<IBareWireInstrumentation>();
        List<string> log = [];

        (ReceiveEndpointRunner runner, ChannelWriter<InboundMessage> writer, ITransportAdapter adapter)
            = CreateRunner(
                [
                    new ConsumerRegistration(
                        typeof(UnhandledOrderEuConsumer),
                        typeof(UnhandledOrder),
                        RoutingKeys: ["orders.eu.*"]),
                ],
                new Dictionary<Type, object>
                {
                    [typeof(UnhandledOrderEuConsumer)] = new UnhandledOrderEuConsumer(log),
                },
                CreateMockDeserializerResolver(new UnhandledOrder("ord-1")),
                NullLogger<ReceiveEndpointRunner>.Instance,
                instrumentation);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new()
            {
                ["BW-MessageType"] = nameof(UnhandledOrder),
                ["BW-RoutingKey"] = "orders.us.created",
            }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — metric recorded with the UnhandledDelivery error_type category.
        instrumentation.Received(1).RecordFailure(EndpointName, "unknown", "UnhandledDelivery");

        // Assert — message settled with Reject (unhandled delivery is rejected, not acked).
        await adapter.Received(1).SettleAsync(
            SettlementAction.Reject,
            Arg.Any<InboundMessage>(),
            Arg.Any<CancellationToken>());
    }

    // ── Test 2: no raw routing-key leak to logs or metric tags ──────────────────────────────────

    [Fact]
    public async Task ProcessMessageAsync_UnhandledDeliveryWithSensitiveKey_RoutingKeyNotLeakedToLogsOrMetrics()
    {
        // Arrange — same unhandled scenario but with a sensitive routing key value.
        // ADR-030 §Security: raw BW-RoutingKey is producer-controlled input and must never
        // appear in logs or metric tag values.
        const string sensitiveKey = "tenant.acme.secret";
        IBareWireInstrumentation instrumentation = Substitute.For<IBareWireInstrumentation>();
        CapturingLogger logger = new();
        List<string> log = [];

        (ReceiveEndpointRunner runner, ChannelWriter<InboundMessage> writer, _)
            = CreateRunner(
                [
                    new ConsumerRegistration(
                        typeof(UnhandledOrderEuConsumer),
                        typeof(UnhandledOrder),
                        RoutingKeys: ["orders.eu.*"]),
                ],
                new Dictionary<Type, object>
                {
                    [typeof(UnhandledOrderEuConsumer)] = new UnhandledOrderEuConsumer(log),
                },
                CreateMockDeserializerResolver(new UnhandledOrder("ord-1")),
                logger,
                instrumentation);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new()
            {
                ["BW-MessageType"] = nameof(UnhandledOrder),
                ["BW-RoutingKey"] = sensitiveKey,
            }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — raw routing key must not appear in any log entry (ADR-030 §Security).
        logger.Entries.Should().NotContain(e => e.Message.Contains(sensitiveKey));

        // Assert — metric recorded with only endpoint / "unknown" / category — no routing key in args.
        instrumentation.Received(1).RecordFailure(EndpointName, "unknown", "UnhandledDelivery");
    }

    // ── Test 3: negative — handled delivery records NO unhandled metric ──────────────────────────

    [Fact]
    public async Task ProcessMessageAsync_DispatchedDelivery_NoUnhandledDeliveryMetricRecorded()
    {
        // Arrange — catch-all consumer (no routing keys) so the legacy path dispatches it successfully.
        IBareWireInstrumentation instrumentation = Substitute.For<IBareWireInstrumentation>();
        List<string> log = [];

        (ReceiveEndpointRunner runner, ChannelWriter<InboundMessage> writer, _)
            = CreateRunner(
                [
                    new ConsumerRegistration(
                        typeof(UnhandledOrderCatchAllConsumer),
                        typeof(UnhandledOrder)), // no routing keys → catch-all legacy path
                ],
                new Dictionary<Type, object>
                {
                    [typeof(UnhandledOrderCatchAllConsumer)] = new UnhandledOrderCatchAllConsumer(log),
                },
                CreateMockDeserializerResolver(new UnhandledOrder("ord-catch")),
                NullLogger<ReceiveEndpointRunner>.Instance,
                instrumentation);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new() { ["BW-MessageType"] = nameof(UnhandledOrder) }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — consumer ran (delivery was dispatched).
        log.Should().ContainSingle().Which.Should().StartWith("CatchAll:");

        // Assert — no UnhandledDelivery metric (delivery was dispatched, not silently dropped).
        instrumentation.DidNotReceive().RecordFailure(
            Arg.Any<string>(), Arg.Any<string>(), "UnhandledDelivery");
    }

    // ── Test 4: inbox-filtered delivery NOT treated as unhandled ────────────────────────────────

    [Fact]
    public async Task ProcessMessageAsync_InboxFilteredDelivery_NoUnhandledMetricAndSettlesAck()
    {
        // Arrange — DI middleware short-circuits without dispatching and sets InboxFiltered.
        // The runner startup probe sees the middleware → _hasDiMiddleware = true, so the DI
        // middleware path is taken per-message. The delivery must be treated as dedup (Ack),
        // NOT as an unhandled delivery (Reject + UnhandledDelivery metric).
        IBareWireInstrumentation instrumentation = Substitute.For<IBareWireInstrumentation>();
        InboxFilterMiddleware inboxFilter = new();

        (ReceiveEndpointRunner runner, ChannelWriter<InboundMessage> writer, ITransportAdapter adapter)
            = CreateRunner(
                [
                    new ConsumerRegistration(
                        typeof(UnhandledOrderEuConsumer),
                        typeof(UnhandledOrder),
                        RoutingKeys: ["orders.eu.*"]),
                ],
                new Dictionary<Type, object>
                {
                    [typeof(UnhandledOrderEuConsumer)] = new UnhandledOrderEuConsumer([]),
                },
                CreateMockDeserializerResolver(new UnhandledOrder("ord-dedup")),
                NullLogger<ReceiveEndpointRunner>.Instance,
                instrumentation,
                middlewares: [inboxFilter]);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new()
            {
                ["BW-MessageType"] = nameof(UnhandledOrder),
                ["BW-RoutingKey"] = "orders.us.created", // would be unhandled without inbox filter
            }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — no UnhandledDelivery metric (delivery is dedup-filtered, not truly unhandled).
        instrumentation.DidNotReceive().RecordFailure(
            Arg.Any<string>(), Arg.Any<string>(), "UnhandledDelivery");

        // Assert — settled with Ack (inbox-filtered = dedup, message already processed).
        await adapter.Received(1).SettleAsync(
            SettlementAction.Ack,
            Arg.Any<InboundMessage>(),
            Arg.Any<CancellationToken>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="ReceiveEndpointRunner"/> wired to an in-memory channel.
    /// Adapted from <c>ReceiveEndpointRunnerUntypedDispatchTests.CreateRunner</c> (private static,
    /// GAP-4 — copied/adapted into this class, not referenced directly).
    /// Accepts <paramref name="instrumentation"/> instead of hardcoding <c>new NullInstrumentation()</c>
    /// to enable metric-call verification via NSubstitute.
    /// Returns the adapter mock so tests can assert on <see cref="ITransportAdapter.SettleAsync"/>.
    /// </summary>
    private static (
        ReceiveEndpointRunner Runner,
        ChannelWriter<InboundMessage> Writer,
        ITransportAdapter Adapter) CreateRunner(
        IReadOnlyList<ConsumerRegistration> consumers,
        IReadOnlyDictionary<Type, object> instances,
        IDeserializerResolver deserializerResolver,
        ILogger logger,
        IBareWireInstrumentation instrumentation,
        IReadOnlyList<IMessageMiddleware>? middlewares = null)
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

        // Supply the middleware list for both the startup probe (_hasDiMiddleware detection) and
        // per-message DI resolution. An empty array means no DI middleware (fast path).
        IMessageMiddleware[] mwArray = middlewares is { Count: > 0 } ? [.. middlewares] : [];
        provider.GetService(typeof(IEnumerable<IMessageMiddleware>))
            .Returns(mwArray);

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
            instrumentation,
            logger,
            loggerFactory: NullLoggerFactory.Instance);

        return (runner, channel.Writer, adapter);
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
