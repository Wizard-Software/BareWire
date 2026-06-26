using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Serialization.Json;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

// BareWire and RabbitMQ.Client both expose an ExchangeType; the topology API uses BareWire's.
using ExchangeType = BareWire.Abstractions.ExchangeType;

namespace BareWire.IntegrationTests.Transport;

// ── Shared message records ──────────────────────────────────────────────────────
// Public so ConsumerInvokerFactory + DI can resolve them at runtime.

/// <summary>Typed message shared by several consumers — exercises most-specific-wins selection.</summary>
public sealed record TransferInitiated(string CorrelationId);

/// <summary>Typed message for the topic-wildcard scenario (<c>*</c> vs <c>#</c>).</summary>
public sealed record OrderEvent(string OrderId);

/// <summary>
/// Target shape for the type-less interop scenario. A foreign (non-BareWire) producer publishes
/// plain JSON with these fields and NO message-type header; an <c>AcceptUntyped()</c> consumer
/// deserializes it raw-first into this record.
/// </summary>
public sealed record ForeignPayload(string Id, string Note);

// ── Dispatch sink ───────────────────────────────────────────────────────────────

/// <summary>
/// Single hit recorded by a consumer when it is selected for a delivery: which consumer ran,
/// the delivery routing key (read from the inbound <c>BW-RoutingKey</c> header the RabbitMQ
/// adapter stamps from the AMQP routing key), and the deserialized payload.
/// </summary>
public sealed record DispatchHit(string Consumer, string RoutingKey, object Payload);

/// <summary>
/// Thread-safe collector shared (as a DI singleton) by every test consumer. Provides a polling
/// barrier so a test can wait deterministically for an exact number of dispatches rather than
/// sleeping a fixed amount.
/// </summary>
public sealed class DispatchSink
{
    private readonly ConcurrentQueue<DispatchHit> _hits = new();

    public void Record(string consumer, IReadOnlyDictionary<string, string> headers, object payload)
    {
        headers.TryGetValue("BW-RoutingKey", out string? routingKey);
        _hits.Enqueue(new DispatchHit(consumer, routingKey ?? string.Empty, payload));
    }

    public int Count => _hits.Count;

    public IReadOnlyList<DispatchHit> Hits => _hits.ToArray();

    /// <summary>Routing keys received by a single named consumer (in arrival order).</summary>
    public IReadOnlyList<string> RoutingKeysFor(string consumer) =>
        _hits.Where(h => h.Consumer == consumer).Select(h => h.RoutingKey).ToArray();

    /// <summary>
    /// Polls until at least <paramref name="expected"/> hits are recorded or the timeout elapses.
    /// Returns the final reached state (caller asserts the exact count after a short stabilisation
    /// window to catch a stray over-delivery still in flight).
    /// </summary>
    public async Task<bool> WaitForCountAsync(int expected, TimeSpan timeout, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (_hits.Count >= expected)
            {
                return true;
            }

            await Task.Delay(50, ct);
        }

        return _hits.Count >= expected;
    }
}

// ── Test consumers ──────────────────────────────────────────────────────────────
// Each records its own selection into the shared sink. Patterns are attached at registration
// time via the IConsumerConfigurator overload, NOT inside the consumer.

public sealed class RegionEuTransferConsumer(DispatchSink sink) : IConsumer<TransferInitiated>
{
    public Task ConsumeAsync(ConsumeContext<TransferInitiated> context)
    {
        sink.Record(nameof(RegionEuTransferConsumer), context.Headers, context.Message);
        return Task.CompletedTask;
    }
}

public sealed class RegionGlobalTransferConsumer(DispatchSink sink) : IConsumer<TransferInitiated>
{
    public Task ConsumeAsync(ConsumeContext<TransferInitiated> context)
    {
        sink.Record(nameof(RegionGlobalTransferConsumer), context.Headers, context.Message);
        return Task.CompletedTask;
    }
}

public sealed class OrderStarConsumer(DispatchSink sink) : IConsumer<OrderEvent>
{
    public Task ConsumeAsync(ConsumeContext<OrderEvent> context)
    {
        sink.Record(nameof(OrderStarConsumer), context.Headers, context.Message);
        return Task.CompletedTask;
    }
}

public sealed class AuditHashConsumer(DispatchSink sink) : IConsumer<OrderEvent>
{
    public Task ConsumeAsync(ConsumeContext<OrderEvent> context)
    {
        sink.Record(nameof(AuditHashConsumer), context.Headers, context.Message);
        return Task.CompletedTask;
    }
}

/// <summary>Opts into type-less delivery via <c>AcceptUntyped()</c> (set at registration).</summary>
public sealed class ForeignAcceptingConsumer(DispatchSink sink) : IConsumer<ForeignPayload>
{
    public Task ConsumeAsync(ConsumeContext<ForeignPayload> context)
    {
        sink.Record(nameof(ForeignAcceptingConsumer), context.Headers, context.Message);
        return Task.CompletedTask;
    }
}

/// <summary>Has a matching pattern but NO <c>AcceptUntyped()</c> — must never catch type-less deliveries.</summary>
public sealed class ForeignTypedOnlyConsumer(DispatchSink sink) : IConsumer<ForeignPayload>
{
    public Task ConsumeAsync(ConsumeContext<ForeignPayload> context)
    {
        sink.Record(nameof(ForeignTypedOnlyConsumer), context.Headers, context.Message);
        return Task.CompletedTask;
    }
}

// ── Test class ──────────────────────────────────────────────────────────────────

/// <summary>
/// End-to-end integration tests for consume-time routing-key dispatch on a real RabbitMQ broker
/// (provisioned via <see cref="AspireFixture"/>). All scenarios share one queue bound to a topic
/// exchange with the catch-all binding key <c>#</c>, so every delivery reaches the queue and the
/// split across consumers happens CLIENT-SIDE at dispatch (manual topology — the binding does not
/// pre-filter by routing key). The producer is a raw RabbitMQ.Client channel so the test controls
/// both the AMQP routing key and the AMQP <c>Type</c> property:
/// <list type="bullet">
///   <item><description><c>Type</c> set → typed delivery (round-trips to the inbound
///   <c>BW-MessageType</c> header) → type+pattern selection, most-specific-wins.</description></item>
///   <item><description><c>Type</c> omitted → type-less delivery (no <c>BW-MessageType</c>) →
///   only a consumer that opted in with <c>AcceptUntyped()</c> can catch it, raw-first.</description></item>
/// </list>
/// Each test uses Guid-suffixed exchange/queue names for isolation.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMqRoutingKeyDispatchTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    private readonly AspireFixture _fixture = fixture;

    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(30);

    // Short window after the expected count is reached, to let any stray over-delivery surface
    // before asserting the EXACT total (guards the disjointness assertions against a false pass).
    private static readonly TimeSpan StabilisationWindow = TimeSpan.FromMilliseconds(400);

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds and starts a full BareWire bus on the real broker: declares a topic exchange + one
    /// shared queue bound with <c>#</c>, registers the given consumers on a single receive endpoint,
    /// and returns the running host plus the shared sink. The queue is declared and bound during
    /// <c>StartAsync</c> (topology deploy), so deliveries published immediately after are buffered
    /// by the broker until the consume loop drains them — no fixed start-up sleep is needed.
    /// </summary>
    private async Task<IHost> StartBusAsync(
        string exchange,
        string queue,
        DispatchSink sink,
        Action<IServiceCollection> registerConsumers,
        Action<IReceiveEndpointConfigurator> configureEndpoint,
        CancellationToken ct)
    {
        string connectionString = _fixture.GetRabbitMqConnectionString();

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddSingleton(sink);
                registerConsumers(services);

                // Raw-first JSON (ADR-001) — required for type-less raw deserialization.
                services.AddBareWireJsonSerializer();

                services.AddBareWireRabbitMq(rmq =>
                {
                    rmq.Host(connectionString);
                    rmq.DefaultExchange(exchange);

                    // Manual topology (ADR-002): topic exchange + shared queue bound with the
                    // catch-all key '#', so EVERY routing key lands in the queue and the routing-key
                    // split is performed client-side at dispatch (not by the broker binding).
                    rmq.ConfigureTopology(t =>
                    {
                        t.DeclareExchange(exchange, ExchangeType.Topic, durable: false, autoDelete: false);
                        t.DeclareQueue(queue, durable: false, autoDelete: false);
                        t.BindExchangeToQueue(exchange, queue, routingKey: "#");
                    });

                    rmq.ReceiveEndpoint(queue, configureEndpoint);
                });

                // Core bus engine (registers IBus + the hosted consume service). AddBareWireRabbitMq
                // above registers the transport adapter and flags it, so an empty bus configurator
                // passes validation — identical to what the AddBareWireWithRabbitMq bundle does.
                services.AddBareWire(_ => { });
            })
            .Build();

        await host.StartAsync(ct);
        return host;
    }

    /// <summary>One raw delivery: routing key, AMQP <c>Type</c> (null ⇒ type-less), and JSON body.</summary>
    private readonly record struct RawDelivery(string RoutingKey, string? MessageType, string Json);

    /// <summary>
    /// Publishes the given deliveries IN ORDER over a SINGLE raw RabbitMQ.Client channel with
    /// publisher confirms enabled. One channel + confirms makes the ordering guarantee airtight:
    /// per-channel AMQP FIFO holds, and <c>BasicPublishAsync</c> only returns once the broker has
    /// confirmed the publish, so delivery N is provably enqueued before delivery N+1 is sent. This
    /// is the foundation the sentinel (negative control) and the "unmatched-published-first"
    /// (wildcard) tests rely on. <see cref="RawDelivery.MessageType"/> is written to the AMQP
    /// <c>Type</c> property: non-null simulates a typed (BareWire-style) producer; null simulates a
    /// foreign producer emitting plain JSON with no type header.
    /// </summary>
    private async Task PublishInOrderAsync(string exchange, CancellationToken ct, params RawDelivery[] deliveries)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(_fixture.GetRabbitMqConnectionString()),
            AutomaticRecoveryEnabled = false,
        };

        await using IConnection connection = await factory.CreateConnectionAsync(ct);
        await using IChannel channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);

        foreach (RawDelivery delivery in deliveries)
        {
            var props = new BasicProperties
            {
                ContentType = "application/json",
                Type = delivery.MessageType, // null ⇒ type-less foreign delivery (no BW-MessageType inbound)
            };

            await channel.BasicPublishAsync<BasicProperties>(
                exchange: exchange,
                routingKey: delivery.RoutingKey,
                mandatory: false,
                basicProperties: props,
                body: Encoding.UTF8.GetBytes(delivery.Json),
                cancellationToken: ct);
        }
    }

    private static string Suffix() => Guid.NewGuid().ToString("N");

    // ── Scenario 1: multiple consumers, one queue, most-specific-wins ─────────────

    /// <summary>
    /// Two consumers of the SAME type share one queue, split by routing key. <c>transfer.eu.*</c> is
    /// more specific than <c>transfer.#</c>, so a delivery matching both goes to the EU consumer;
    /// a delivery matching only the broad pattern goes to the global consumer. Proves deterministic
    /// most-specific-wins selection on live AMQP deliveries.
    /// </summary>
    [Fact]
    public async Task MultipleConsumers_SharedQueue_MostSpecificWins()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string suffix = Suffix();
        string exchange = $"rk-mostspecific-ex-{suffix}";
        string queue = $"rk-mostspecific-q-{suffix}";
        var sink = new DispatchSink();

        IHost host = await StartBusAsync(
            exchange, queue, sink,
            services =>
            {
                services.AddTransient<RegionEuTransferConsumer>();
                services.AddTransient<RegionGlobalTransferConsumer>();
            },
            e =>
            {
                e.Consumer<RegionEuTransferConsumer, TransferInitiated>(c => c.RoutingKeys("transfer.eu.*"));
                e.Consumer<RegionGlobalTransferConsumer, TransferInitiated>(c => c.RoutingKeys("transfer.#"));
            },
            cts.Token);

        try
        {
            // All three are typed (Type = "TransferInitiated"); each matches at least one pattern.
            await PublishInOrderAsync(exchange, cts.Token,
                new RawDelivery("transfer.eu.payment", "TransferInitiated", """{"correlationId":"T-EU-1"}"""),
                new RawDelivery("transfer.us.payment", "TransferInitiated", """{"correlationId":"T-US-1"}"""),
                new RawDelivery("transfer.eu.refund", "TransferInitiated", """{"correlationId":"T-EU-2"}"""));

            bool reached = await sink.WaitForCountAsync(3, DispatchTimeout, cts.Token);
            reached.Should().BeTrue(because: "all three typed deliveries must be dispatched to a matching consumer");

            // Stabilise, then assert the EXACT total — a stray 4th dispatch would mean a delivery
            // was double-handled (disjointness violation).
            await Task.Delay(StabilisationWindow, cts.Token);
            sink.Count.Should().Be(3);

            sink.RoutingKeysFor(nameof(RegionEuTransferConsumer))
                .Should().BeEquivalentTo(["transfer.eu.payment", "transfer.eu.refund"],
                    because: "transfer.eu.* is more specific than transfer.# for eu.* keys (most-specific-wins)");

            sink.RoutingKeysFor(nameof(RegionGlobalTransferConsumer))
                .Should().BeEquivalentTo(["transfer.us.payment"],
                    because: "transfer.us.payment matches only the broad transfer.# pattern");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    // ── Scenario 2: topic wildcards on real AMQP deliveries ───────────────────────

    /// <summary>
    /// Verifies the client-side matcher agrees with RabbitMQ topic semantics on real deliveries:
    /// <c>*</c> matches exactly one word (so <c>order.*.created</c> matches <c>order.eu.created</c>
    /// but NOT <c>order.eu.west.created</c>), and <c>#</c> matches zero or more words (so
    /// <c>audit.#</c> matches both <c>audit</c> and <c>audit.user.login</c>).
    /// </summary>
    [Fact]
    public async Task TopicWildcards_RealAmqpDeliveries_MatchBrokerSemantics()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string suffix = Suffix();
        string exchange = $"rk-wildcards-ex-{suffix}";
        string queue = $"rk-wildcards-q-{suffix}";
        var sink = new DispatchSink();

        IHost host = await StartBusAsync(
            exchange, queue, sink,
            services =>
            {
                services.AddTransient<OrderStarConsumer>();
                services.AddTransient<AuditHashConsumer>();
            },
            e =>
            {
                e.Consumer<OrderStarConsumer, OrderEvent>(c => c.RoutingKeys("order.*.created"));
                e.Consumer<AuditHashConsumer, OrderEvent>(c => c.RoutingKeys("audit.#"));
            },
            cts.Token);

        try
        {
            // Published FIRST and unmatched (three words vs the single-word '*'): no consumer catches
            // it. Published over ONE channel with confirms ahead of the three matched deliveries, so
            // by per-channel FIFO + sequential dispatch this one is provably processed (and left
            // undispatched) before any matched hit — the "not in OrderStar" assertion cannot be a
            // false pass on a lost/late delivery.
            await PublishInOrderAsync(exchange, cts.Token,
                new RawDelivery("order.eu.west.created", "OrderEvent", """{"orderId":"O-WEST"}"""),
                new RawDelivery("order.eu.created", "OrderEvent", """{"orderId":"O-EU"}"""),
                new RawDelivery("audit", "OrderEvent", """{"orderId":"A-ROOT"}"""),
                new RawDelivery("audit.user.login", "OrderEvent", """{"orderId":"A-LOGIN"}"""));

            bool reached = await sink.WaitForCountAsync(3, DispatchTimeout, cts.Token);
            reached.Should().BeTrue(because: "the three keys that match a pattern must be dispatched");

            await Task.Delay(StabilisationWindow, cts.Token);
            sink.Count.Should().Be(3, because: "order.eu.west.created matches no pattern and must stay undispatched");

            sink.RoutingKeysFor(nameof(OrderStarConsumer))
                .Should().BeEquivalentTo(["order.eu.created"],
                    because: "* matches exactly one word, so order.eu.west.created does NOT match order.*.created");

            sink.RoutingKeysFor(nameof(AuditHashConsumer))
                .Should().BeEquivalentTo(["audit", "audit.user.login"],
                    because: "# matches zero or more words, so it matches both 'audit' and 'audit.user.login'");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    // ── Scenario 3: type-less interop (foreign producer → raw JSON) ───────────────

    /// <summary>
    /// A foreign producer publishes plain JSON with NO type header but a routing key. A consumer that
    /// declared <c>AcceptUntyped()</c> and a matching pattern catches it and deserializes the raw
    /// payload (raw-first) into its declared message type. Proves deterministic type-less selection.
    /// </summary>
    [Fact]
    public async Task TypeLessInterop_ForeignJsonWithoutMessageType_CaughtByAcceptUntyped()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string suffix = Suffix();
        string exchange = $"rk-typeless-ex-{suffix}";
        string queue = $"rk-typeless-q-{suffix}";
        var sink = new DispatchSink();

        IHost host = await StartBusAsync(
            exchange, queue, sink,
            services => services.AddTransient<ForeignAcceptingConsumer>(),
            e => e.Consumer<ForeignAcceptingConsumer, ForeignPayload>(c =>
            {
                c.RoutingKeys("ext.transfer.*");
                c.AcceptUntyped();
            }),
            cts.Token);

        try
        {
            // messageType: null ⇒ no AMQP Type property ⇒ no inbound BW-MessageType ⇒ type-less path.
            await PublishInOrderAsync(exchange, cts.Token,
                new RawDelivery("ext.transfer.eu", MessageType: null, """{"id":"X-1","note":"from-foreign"}"""));

            bool reached = await sink.WaitForCountAsync(1, DispatchTimeout, cts.Token);
            reached.Should().BeTrue(because: "the AcceptUntyped() consumer must catch the foreign type-less delivery");

            await Task.Delay(StabilisationWindow, cts.Token);
            sink.Count.Should().Be(1);

            DispatchHit hit = sink.Hits.Single();
            hit.Consumer.Should().Be(nameof(ForeignAcceptingConsumer));
            hit.RoutingKey.Should().Be("ext.transfer.eu");
            hit.Payload.Should().BeOfType<ForeignPayload>()
                .Which.Should().Be(new ForeignPayload("X-1", "from-foreign"),
                    because: "the raw foreign JSON is deserialized raw-first into the consumer's TMessage");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    // ── Scenario 4: opt-in negative control (secure-by-default) ───────────────────

    /// <summary>
    /// Secure-by-default: a consumer with a matching pattern but WITHOUT <c>AcceptUntyped()</c> must
    /// NOT catch a type-less delivery (its pattern narrows typed dispatch only). The assertion is
    /// made delivery-safe with a sentinel: a SECOND type-less delivery that a co-located
    /// <c>AcceptUntyped()</c> consumer DOES accept is published right after the negative one. Both land
    /// on one FIFO queue with sequential dispatch, so once the sentinel is observed the negative
    /// delivery has provably already been processed (and left undispatched) — an empty typed-only sink
    /// is therefore a genuine rejection, not a lost message.
    /// </summary>
    [Fact]
    public async Task TypeLessOptInNegative_PatternWithoutAcceptUntyped_NotSelected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string suffix = Suffix();
        string exchange = $"rk-optin-neg-ex-{suffix}";
        string queue = $"rk-optin-neg-q-{suffix}";
        var sink = new DispatchSink();

        IHost host = await StartBusAsync(
            exchange, queue, sink,
            services =>
            {
                services.AddTransient<ForeignTypedOnlyConsumer>();
                services.AddTransient<ForeignAcceptingConsumer>();
            },
            e =>
            {
                // Negative subject: matching pattern, but no AcceptUntyped() → never a type-less candidate.
                e.Consumer<ForeignTypedOnlyConsumer, ForeignPayload>(c => c.RoutingKeys("ext.foreign.*"));
                // Sentinel: opted in on a disjoint pattern, proves the broker delivered + dispatched.
                e.Consumer<ForeignAcceptingConsumer, ForeignPayload>(c =>
                {
                    c.RoutingKeys("ext.sentinel.*");
                    c.AcceptUntyped();
                });
            },
            cts.Token);

        try
        {
            // Published over ONE channel with confirms, in order, so per-channel FIFO holds:
            //   1) Negative subject (type-less): matches ForeignTypedOnlyConsumer's pattern but it has
            //      no AcceptUntyped() → must be left undispatched.
            //   2) Sentinel (type-less): only the AcceptUntyped() consumer can catch it. Observing it
            //      proves the negative subject was already delivered and processed (rejected) first.
            await PublishInOrderAsync(exchange, cts.Token,
                new RawDelivery("ext.foreign.eu", MessageType: null, """{"id":"F-1","note":"should-not-be-caught"}"""),
                new RawDelivery("ext.sentinel.eu", MessageType: null, """{"id":"S-1","note":"sentinel"}"""));

            bool reached = await sink.WaitForCountAsync(1, DispatchTimeout, cts.Token);
            reached.Should().BeTrue(because: "the sentinel proves the broker delivered and the loop dispatched both messages");

            await Task.Delay(StabilisationWindow, cts.Token);

            // The sentinel arrived; by FIFO + sequential dispatch the negative subject was processed first.
            sink.RoutingKeysFor(nameof(ForeignTypedOnlyConsumer))
                .Should().BeEmpty(because: "a consumer without AcceptUntyped() must never catch a type-less delivery (secure-by-default)");

            sink.RoutingKeysFor(nameof(ForeignAcceptingConsumer))
                .Should().BeEquivalentTo(["ext.sentinel.eu"],
                    because: "only the opted-in consumer catches its matching type-less delivery");

            sink.Count.Should().Be(1, because: "exactly one of the two type-less deliveries is dispatched");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }
}
