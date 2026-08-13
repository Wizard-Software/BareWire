using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Serialization.Json;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

// BareWire and RabbitMQ.Client both expose an ExchangeType; the topology API uses BareWire's.
using ExchangeType = BareWire.Abstractions.ExchangeType;

namespace BareWire.IntegrationTests.Transport;

// ── Shared message records (public so ConsumerInvokerFactory + DI can resolve them at runtime) ──

/// <summary>Message handled by the definition-driven consumer in Test A (retry + dead-letter).</summary>
public sealed record DefTransfer(string CorrelationId);

/// <summary>Message handled by the opt-in-topology consumer in Test B.</summary>
public sealed record DefTopologyEvent(string Id);

/// <summary>Message handled by the axis-separation consumers in Test C.</summary>
public sealed record DefOrder(string OrderId);

// ── Shared hit sink with a polling barrier (deterministic wait instead of a fixed sleep) ────────

/// <summary>
/// Thread-safe collector shared (as a DI singleton) by the test consumers. Records one hit per
/// dispatched delivery (consumer name + inbound routing key) and offers a polling barrier so a test
/// waits for an exact number of dispatches rather than sleeping a fixed amount.
/// </summary>
public sealed class DefHitSink
{
    private readonly ConcurrentQueue<(string Consumer, string RoutingKey)> _hits = new();

    public void Record(string consumer, IReadOnlyDictionary<string, string> headers)
    {
        headers.TryGetValue("BW-RoutingKey", out string? routingKey);
        _hits.Enqueue((consumer, routingKey ?? string.Empty));
    }

    public int Count => _hits.Count;

    public IReadOnlyList<string> RoutingKeysFor(string consumer) =>
        _hits.Where(h => h.Consumer == consumer).Select(h => h.RoutingKey).ToArray();

    /// <summary>Polls until at least <paramref name="expected"/> hits are recorded or the timeout elapses.</summary>
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

// ── Test A consumer + definition ────────────────────────────────────────────────────────────────

/// <summary>
/// Consumer whose per-consumer routing keys are supplied ENTIRELY by a DI-registered
/// <see cref="ConsumerDefinition{TConsumer}"/> (Test A). Throws every time to exercise the
/// retry → dead-letter path on a live broker.
/// </summary>
public sealed class DefTransferConsumer(DefHitSink sink) : IConsumer<DefTransfer>
{
    public Task ConsumeAsync(ConsumeContext<DefTransfer> context)
    {
        sink.Record(nameof(DefTransferConsumer), context.Headers);
        throw new InvalidOperationException("Simulated poison failure — exercises retry + dead-letter.");
    }
}

/// <summary>
/// Colocated per-consumer settings for <see cref="DefTransferConsumer"/>: a single routing-key
/// pattern (<c>transfer.eu.*</c>). Discovered by DI registration at start-up (no assembly scan) and
/// merged into the consumer's registration — this is the axis under test in Test A.
/// </summary>
public sealed class DefTransferConsumerDefinition : ConsumerDefinition<DefTransferConsumer>
{
    protected override void Configure(
        IReceiveEndpointConfigurator endpoint,
        IConsumerConfigurator<DefTransferConsumer> consumer)
    {
        consumer.RoutingKeys("transfer.eu.*");
    }
}

// ── Test B consumer (opt-in topology helper) ────────────────────────────────────────────────────

/// <summary>Records deliveries for the opt-in-topology scenario (Test B).</summary>
public sealed class DefTopologyConsumer(DefHitSink sink) : IConsumer<DefTopologyEvent>
{
    public Task ConsumeAsync(ConsumeContext<DefTopologyEvent> context)
    {
        sink.Record(nameof(DefTopologyConsumer), context.Headers);
        return Task.CompletedTask;
    }
}

// ── Test C consumers (routing-key ↔ binding axis separation) ────────────────────────────────────

/// <summary>Narrow dispatcher pattern <c>orders.priority.*</c> — must never see a delivery the binding did not route.</summary>
public sealed class DefOrderPriorityConsumer(DefHitSink sink) : IConsumer<DefOrder>
{
    public Task ConsumeAsync(ConsumeContext<DefOrder> context)
    {
        sink.Record(nameof(DefOrderPriorityConsumer), context.Headers);
        return Task.CompletedTask;
    }
}

/// <summary>Broad dispatcher pattern <c>orders.#</c> — sentinel proving the broker actually delivered the bound key.</summary>
public sealed class DefOrderSentinelConsumer(DefHitSink sink) : IConsumer<DefOrder>
{
    public Task ConsumeAsync(ConsumeContext<DefOrder> context)
    {
        sink.Record(nameof(DefOrderSentinelConsumer), context.Headers);
        return Task.CompletedTask;
    }
}

/// <summary>
/// End-to-end integration tests for Feature 19 (ADR-036) on a real RabbitMQ broker (provisioned via
/// <see cref="AspireFixture"/>). Covers three invariants: (A) a DI-discovered
/// <see cref="ConsumerDefinition{TConsumer}"/> composed with endpoint retry + manual dead-letter
/// topology; (B) the opt-in <see cref="ConsumerConfiguratorTopologyExtensions.DeclareTopology{TConsumer, TMessage}"/>
/// helper declares broker entities, and WITHOUT it topology stays unchanged (ADR-002); (C) declaring a
/// dispatcher routing-key pattern does NOT create a broker binding (the two axes stay independent).
/// Each test uses Guid-suffixed exchange/queue names for isolation.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMqConsumerDefinitionTopologyTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    private readonly AspireFixture _fixture = fixture;

    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StabilisationWindow = TimeSpan.FromMilliseconds(600);

    private static string Suffix() => Guid.NewGuid().ToString("N");

    // ── Raw AMQP helpers (control routing key + probe broker state directly) ─────────────────────

    private async Task<IConnection> OpenRawConnectionAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(_fixture.GetRabbitMqConnectionString()),
            AutomaticRecoveryEnabled = false,
        };

        return await factory.CreateConnectionAsync(ct);
    }

    /// <summary>Publishes one JSON delivery with an explicit AMQP routing key + <c>Type</c> header.</summary>
    private async Task PublishAsync(string exchange, string routingKey, string? messageType, string json, CancellationToken ct)
    {
        await using IConnection connection = await OpenRawConnectionAsync(ct);
        await using IChannel channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);

        var props = new BasicProperties { ContentType = "application/json", Type = messageType };
        await channel.BasicPublishAsync(
            exchange: exchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: props,
            body: Encoding.UTF8.GetBytes(json),
            cancellationToken: ct);
    }

    /// <summary>True if the queue exists on the broker (passive declare succeeds), false if it does not.</summary>
    private async Task<bool> QueueExistsAsync(string queue, CancellationToken ct)
    {
        await using IConnection connection = await OpenRawConnectionAsync(ct);
        // Passive declare throws (and closes the channel) when the queue is absent — a per-channel probe.
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: ct);
        try
        {
            await channel.QueueDeclarePassiveAsync(queue, ct);
            return true;
        }
        catch (OperationInterruptedException)
        {
            return false;
        }
    }

    /// <summary>Reads one message from a queue (for DLQ verification); returns the UTF-8 body or null on timeout.</summary>
    private async Task<string?> TryConsumeOneBodyAsync(string queue, TimeSpan timeout, CancellationToken ct)
    {
        await using IConnection connection = await OpenRawConnectionAsync(ct);
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: ct);

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            BasicGetResult? result = await channel.BasicGetAsync(queue, autoAck: true, ct);
            if (result is not null)
            {
                return Encoding.UTF8.GetString(result.Body.ToArray());
            }

            await Task.Delay(100, ct);
        }

        return null;
    }

    /// <summary>Builds + starts a BareWire host on the real broker with the given registration + endpoint config.</summary>
    private async Task<IHost> StartHostAsync(
        Action<IServiceCollection> registerServices,
        Action<IRabbitMqConfigurator> configureRmq,
        CancellationToken ct)
    {
        string connectionString = _fixture.GetRabbitMqConnectionString();

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                registerServices(services);
                services.AddBareWireJsonSerializer();
                services.AddBareWireRabbitMq(rmq =>
                {
                    rmq.Host(connectionString);
                    configureRmq(rmq);
                });
                services.AddBareWire(_ => { });
            })
            .Build();

        await host.StartAsync(ct);
        return host;
    }

    // ── Test A: ConsumerDefinition (routing keys) + retry + dead-letter compose on a live broker ─

    /// <summary>
    /// A DI-registered <see cref="DefTransferConsumerDefinition"/> supplies the consumer's routing-key
    /// pattern (<c>transfer.eu.*</c>). The endpoint sets bounded retry; manual topology declares a
    /// dead-letter exchange/queue and the source queue's dead-letter arguments. A poison delivery on a
    /// key that matches the DEFINITION pattern is retried and ends up in the DLQ, while a delivery that
    /// reaches the queue (via the <c>#</c> binding) but does NOT match the definition pattern is never
    /// dispatched — proving DI-discovery, retry and dead-letter compose end-to-end and that the
    /// definition-supplied routing key governs dispatch.
    /// </summary>
    [Fact]
    public async Task ConsumerDefinition_WithRetryAndDeadLetter_ComposeOnLiveBroker()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string suffix = Suffix();
        string exchange = $"def-a-ex-{suffix}";
        string srcQueue = $"def-a-src-{suffix}";
        string dlx = $"def-a-dlx-{suffix}";
        string dlq = $"def-a-dlq-{suffix}";
        var sink = new DefHitSink();

        IHost host = await StartHostAsync(
            services =>
            {
                services.AddSingleton(sink);
                services.AddTransient<DefTransferConsumer>();
                // DI-based discovery: the definition is found purely by this registration (no assembly scan).
                services.AddSingleton<ConsumerDefinition<DefTransferConsumer>, DefTransferConsumerDefinition>();
            },
            rmq =>
            {
                rmq.DefaultExchange(exchange);

                // Manual topology (ADR-002): topic exchange + shared source queue bound with '#', a DLX,
                // a DLQ bound to the DLX, and the source queue wired to dead-letter into the DLX.
                rmq.ConfigureTopology(t =>
                {
                    t.DeclareExchange(exchange, ExchangeType.Topic, durable: false, autoDelete: false);
                    t.DeclareExchange(dlx, ExchangeType.Direct, durable: false, autoDelete: false);
                    t.DeclareQueue(dlq, durable: false, autoDelete: false);
                    t.BindExchangeToQueue(dlx, dlq, routingKey: dlq);
                    t.DeclareQueue(srcQueue, durable: false, autoDelete: false,
                        configure: q => q.DeadLetterExchange(dlx).DeadLetterRoutingKey(dlq));
                    t.BindExchangeToQueue(exchange, srcQueue, routingKey: "#");
                });

                rmq.ReceiveEndpoint(srcQueue, e =>
                {
                    // Endpoint-level bounded retry: the poison is redelivered before being dead-lettered.
                    e.RetryCount = 2;
                    e.RetryInterval = TimeSpan.FromMilliseconds(150);
                    // No-arg overload: ALL routing config comes from the DI-discovered definition.
                    e.Consumer<DefTransferConsumer, DefTransfer>();
                });
            },
            cts.Token);

        try
        {
            // Poison (matches the definition pattern transfer.eu.*) + a non-matching key that still lands
            // in the queue through the '#' binding but must not be dispatched to this consumer.
            await PublishAsync(exchange, "transfer.eu.payment", "DefTransfer", """{"correlationId":"A-EU-1"}""", cts.Token);
            await PublishAsync(exchange, "transfer.us.payment", "DefTransfer", """{"correlationId":"A-US-1"}""", cts.Token);

            // Retry proof: the poison is dispatched at least twice (initial + retry) before dead-lettering.
            bool retried = await sink.WaitForCountAsync(2, DispatchTimeout, cts.Token);
            retried.Should().BeTrue(
                because: "the endpoint RetryCount must redeliver the poison, so the definition-matched key is dispatched more than once");

            await Task.Delay(StabilisationWindow, cts.Token);

            // Definition routing-key governs dispatch: only transfer.eu.* keys reach the consumer.
            sink.RoutingKeysFor(nameof(DefTransferConsumer))
                .Should().OnlyContain(rk => rk == "transfer.eu.payment",
                    because: "the DI-discovered definition declared transfer.eu.*; transfer.us.payment must never be dispatched");

            // Dead-letter proof: after retries are exhausted, a copy of the poison lands in the DLQ.
            string? dlqBody = await TryConsumeOneBodyAsync(dlq, TimeSpan.FromSeconds(20), cts.Token);
            dlqBody.Should().NotBeNull(because: "after retry exhaustion the poison must be dead-lettered to the DLQ");
            dlqBody!.Should().Contain("A-EU-1", because: "the dead-lettered message is the poison payload");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }

    // ── Test B: opt-in topology helper declares entities; without it, topology is unchanged ──────

    /// <summary>
    /// The opt-in <see cref="ConsumerConfiguratorTopologyExtensions.DeclareTopology{TConsumer, TMessage}"/>
    /// helper declares an exchange, a queue and a binding through the transport adapter's deployment path,
    /// so after start-up the queue exists and a message on the binding key reaches the consumer. A second
    /// bus that configures a receive endpoint on a DIFFERENT queue WITHOUT any opt-in topology call leaves
    /// the broker untouched — the queue is never auto-created (ADR-002, manual topology by default).
    /// </summary>
    [Fact]
    public async Task OptInTopologyHelper_DeclaresEntities_AndWithoutIt_TopologyUnchanged()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string suffix = Suffix();
        string exchange = $"def-b-ex-{suffix}";
        string optInQueue = $"def-b-optin-q-{suffix}";
        string noOptInQueue = $"def-b-noopt-q-{suffix}";
        string bindingKey = "topo.created";
        var sink = new DefHitSink();

        // ── Part B1: DeclareTopology creates the entities and delivery works ─────────────────────
        IHost optInHost = await StartHostAsync(
            services =>
            {
                services.AddSingleton(sink);
                services.AddTransient<DefTopologyConsumer>();
            },
            rmq =>
            {
                rmq.DefaultExchange(exchange);
                rmq.ReceiveEndpoint(optInQueue, e =>
                    // Opt-in helper: declares exchange + queue + binding via the transport seam (19.10).
                    e.Consumer<DefTopologyConsumer, DefTopologyEvent>(c =>
                        c.DeclareTopology(exchange, optInQueue, bindingKey, ExchangeType.Topic, durable: false)));
            },
            cts.Token);

        try
        {
            (await QueueExistsAsync(optInQueue, cts.Token))
                .Should().BeTrue(because: "DeclareTopology must create the queue through the transport adapter");

            await PublishAsync(exchange, bindingKey, "DefTopologyEvent", """{"id":"B-1"}""", cts.Token);

            bool delivered = await sink.WaitForCountAsync(1, DispatchTimeout, cts.Token);
            delivered.Should().BeTrue(because: "a message on the declared binding key must reach the opt-in consumer");
        }
        finally
        {
            await optInHost.StopAsync(CancellationToken.None);
            optInHost.Dispose();
        }

        // ── Part B2: no opt-in call ⇒ the broker has no such queue (ADR-002 unchanged) ───────────
        // Receive endpoint on a fresh queue, but NEITHER DeclareTopology NOR ConfigureTopology — manual
        // topology means BareWire must not auto-create the queue. Consuming a never-declared queue may
        // fault start-up (broker 404); that fault is itself consistent with ADR-002, so it is caught
        // specifically and the broker-state invariant is asserted regardless of the start-up path.
        IHost? noOptInHost = null;
        try
        {
            noOptInHost = await StartHostAsync(
                services => services.AddTransient<DefTopologyConsumer>(),
                rmq =>
                {
                    rmq.DefaultExchange(exchange);
                    rmq.ReceiveEndpoint(noOptInQueue, e => e.Consumer<DefTopologyConsumer, DefTopologyEvent>());
                },
                cts.Token);
        }
        catch (OperationInterruptedException)
        {
            // Broker returned 404 for the never-declared consume queue — no auto-topology was performed.
        }

        try
        {
            (await QueueExistsAsync(noOptInQueue, cts.Token))
                .Should().BeFalse(because: "without an explicit opt-in topology declaration no broker entity may be created (ADR-002)");
        }
        finally
        {
            if (noOptInHost is not null)
            {
                await noOptInHost.StopAsync(CancellationToken.None);
                noOptInHost.Dispose();
            }
        }
    }

    // ── Test C: a dispatcher routing-key pattern does NOT create a broker binding ────────────────

    /// <summary>
    /// The consumer's dispatcher routing-key pattern and the broker binding key are SEPARATE axes.
    /// A queue is bound to the topic exchange with the NARROW key <c>orders.audit.*</c> only. A consumer
    /// declares the DISJOINT dispatcher pattern <c>orders.priority.*</c>. A delivery on
    /// <c>orders.priority.high</c> (matches the dispatcher pattern but NOT the binding) never reaches the
    /// queue — proving declaring <see cref="IConsumerConfigurator{TConsumer}.RoutingKeys"/> created no
    /// broker binding. A delivery on <c>orders.audit.login</c> (matches the binding) reaches the queue and
    /// is dispatched only to the broad sentinel, never to the priority consumer.
    /// </summary>
    [Fact]
    public async Task RoutingKeyPattern_DoesNotCreateBrokerBinding_AxesIndependent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string suffix = Suffix();
        string exchange = $"def-c-ex-{suffix}";
        string queue = $"def-c-q-{suffix}";
        var sink = new DefHitSink();

        IHost host = await StartHostAsync(
            services =>
            {
                services.AddSingleton(sink);
                services.AddTransient<DefOrderPriorityConsumer>();
                services.AddTransient<DefOrderSentinelConsumer>();
            },
            rmq =>
            {
                rmq.DefaultExchange(exchange);

                // Manual topology: bind the queue with the NARROW key orders.audit.* ONLY. The consumers'
                // dispatcher patterns below are a separate axis and must not influence this binding.
                rmq.ConfigureTopology(t =>
                {
                    t.DeclareExchange(exchange, ExchangeType.Topic, durable: false, autoDelete: false);
                    t.DeclareQueue(queue, durable: false, autoDelete: false);
                    t.BindExchangeToQueue(exchange, queue, routingKey: "orders.audit.*");
                });

                rmq.ReceiveEndpoint(queue, e =>
                {
                    // Disjoint dispatcher pattern — declaring it must NOT create a broker binding for it.
                    e.Consumer<DefOrderPriorityConsumer, DefOrder>(c => c.RoutingKeys("orders.priority.*"));
                    // Broad sentinel — proves the broker delivered the bound key.
                    e.Consumer<DefOrderSentinelConsumer, DefOrder>(c => c.RoutingKeys("orders.#"));
                });
            },
            cts.Token);

        try
        {
            // priority.high matches the priority consumer's DISPATCH pattern but NOT the binding → not routed.
            await PublishAsync(exchange, "orders.priority.high", "DefOrder", """{"orderId":"C-PRIO"}""", cts.Token);
            // audit.login matches the binding → reaches the queue; dispatched to the sentinel only.
            await PublishAsync(exchange, "orders.audit.login", "DefOrder", """{"orderId":"C-AUDIT"}""", cts.Token);

            bool reached = await sink.WaitForCountAsync(1, DispatchTimeout, cts.Token);
            reached.Should().BeTrue(because: "the sentinel proves the broker delivered the bound orders.audit.login key");

            await Task.Delay(StabilisationWindow, cts.Token);

            sink.RoutingKeysFor(nameof(DefOrderPriorityConsumer))
                .Should().BeEmpty(because: "orders.priority.high matched no binding, so declaring RoutingKeys created no broker binding for it");

            sink.RoutingKeysFor(nameof(DefOrderSentinelConsumer))
                .Should().BeEquivalentTo(["orders.audit.login"],
                    because: "only the bound key reaches the queue, and only the broad sentinel pattern matches it");

            sink.Count.Should().Be(1, because: "exactly one delivery (orders.audit.login) was routed by the binding and dispatched");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }
}
