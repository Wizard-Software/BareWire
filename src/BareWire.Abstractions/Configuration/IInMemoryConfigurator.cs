using BareWire.Abstractions.Topology;

namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Provides a typed fluent API for configuring the in-memory transport layer on a BareWire bus.
/// Obtained via a registration method such as <c>AddBareWireInMemory</c> / <c>AddBareWireWithInMemory</c>
/// during application startup.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Delivery guarantee — explicitly at-most-once.</strong> The in-memory transport keeps every
/// queued and in-flight message in process memory only. A process restart or crash discards them
/// unconditionally — there is no persistence, no write-ahead log, and no recovery on the next start.
/// All publishers and consumers configured through this interface run inside a single process and a
/// single dependency-injection container; there is no cross-process or cross-machine delivery. When a
/// queue is full, the send call reports the message as unaccepted (<see cref="Transport.SendResult.IsConfirmed"/>
/// equal to <see langword="false"/>) rather than blocking indefinitely or throwing. See "Where rejections
/// take effect" below for how that outcome is actually observed by a caller. When at-least-once delivery
/// is required, use a broker-backed transport instead of the in-memory transport.
/// </para>
/// <para>
/// <strong>Trust boundary.</strong> All publishers and consumers wired through this configurator share
/// the trust boundary of the hosting process. Topology and the default exchange only affect message
/// routing — they do not restrict which module in the process may publish to or consume from a given
/// queue. When isolation between publishers and consumers is required, enforce it with an authorization
/// middleware or use a broker transport with its own access-control model; sensitive payload content
/// still requires application-level encryption regardless of transport. Header name mapping and header
/// allow-listing are not available on the in-memory transport; application headers are passed through
/// without filtering.
/// </para>
/// <para>
/// <strong>Routing semantics.</strong> Exchange and queue declaration, default-exchange resolution, and
/// per-type routing mappings follow the same manual-topology model as the broker-backed transports —
/// switching between the in-memory transport and a broker transport is a registration change, not a
/// code change, for consumers and for <see cref="Topology.TopologyDeclaration"/>.
/// </para>
/// <para>
/// <strong>Where rejections take effect.</strong> <see cref="Transport.SendResult.IsConfirmed"/> equal to
/// <see langword="false"/> — from a full queue, an oversized message body, an undeclared or unresolved
/// exchange, or an unroutable message when <see cref="GuaranteedRouting"/> is enabled — is acted upon only
/// by components that inspect the send result; chiefly the transactional outbox dispatcher, which treats a
/// rejection as non-delivery and retries. The direct <c>IBus.PublishAsync</c> / send path is fire-and-forget:
/// the caller does not receive the send result and the bus publishing loop does not retry it, so on that
/// path a rejection is visible only in the transport's logs and metrics, not to the caller. At-least-once
/// delivery still requires a broker-backed transport — the outbox is not an alternative to a broker, only a
/// retry mechanism layered on top of one.
/// </para>
/// </remarks>
public interface IInMemoryConfigurator
{
    /// <summary>
    /// Configures the in-memory topology (exchanges, queues, bindings) that will be registered
    /// in process when the bus is built.
    /// Topology must be declared explicitly (manual topology — nothing is auto-declared), unless
    /// <see cref="AutoDeclareEndpointQueues"/> is enabled for receive-endpoint queues.
    /// </summary>
    /// <remarks>
    /// The topology is registered in process — there is no broker to deploy it to. It is sealed once
    /// the bus has started: declaring it again with a set of declarations that is not identical to the
    /// one already registered throws <see cref="Exceptions.BareWireConfigurationException"/> at bus
    /// startup (during <c>Build()</c>). Declaring an exchange of type <see cref="ExchangeType.Headers"/>
    /// is not supported by the in-memory transport; the topology fails fast with
    /// <see cref="Exceptions.BareWireTransportException"/> when it is deployed/registered at bus startup.
    /// </remarks>
    /// <param name="configure">
    /// A delegate that receives an <see cref="ITopologyConfigurator"/> and declares the topology.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    void ConfigureTopology(Action<ITopologyConfigurator> configure);

    /// <summary>
    /// Registers a receive endpoint (queue consumer) on the bus.
    /// Multiple calls accumulate endpoints — each <paramref name="queueName"/> becomes
    /// an independent consumer binding.
    /// </summary>
    /// <remarks>
    /// The queue named by <paramref name="queueName"/> must already be declared via
    /// <see cref="ConfigureTopology"/>, unless <see cref="AutoDeclareEndpointQueues"/> is enabled — in
    /// that case a queue with no exchanges or bindings is declared for this endpoint automatically at
    /// startup. Otherwise, an undeclared queue name causes <see cref="Exceptions.BareWireConfigurationException"/>
    /// to be thrown at bus startup (during <c>Build()</c>).
    /// </remarks>
    /// <param name="queueName">
    /// The name of the queue to consume from. Must not be <see langword="null"/> or empty.
    /// </param>
    /// <param name="configure">
    /// A delegate that receives an <see cref="IReceiveEndpointConfigurator"/> and applies
    /// consumer, concurrency, and retry settings for this endpoint.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="queueName"/> is <see langword="null"/> or empty.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    void ReceiveEndpoint(string queueName, Action<IReceiveEndpointConfigurator> configure);

    /// <summary>
    /// Sets the default exchange name used by <c>PublishAsync</c> when no <c>BW-Exchange</c>
    /// header is present on the outbound message and no per-type mapping applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This mapping participates in the following precedence order (highest to lowest):
    /// an explicit <c>BW-Exchange</c> header passed to <c>PublishAsync</c>; a type→exchange mapping
    /// registered via <see cref="MapExchange{T}"/> or <see cref="Publish{T}"/>; the default exchange
    /// configured here. When none of these resolve an exchange, the message is published without an
    /// exchange.
    /// </para>
    /// <para>
    /// Passing an empty string selects the built-in default exchange, which routes a message directly
    /// to the queue whose name matches the message's resolved routing key — no separate declaration is
    /// required for the empty-string exchange itself. Any other name must match an exchange declared
    /// via <see cref="ConfigureTopology"/>; an undeclared exchange name causes
    /// <see cref="Exceptions.BareWireConfigurationException"/> at bus startup (during <c>Build()</c>).
    /// At publish time, if the resolved exchange no longer exists, only that message is rejected
    /// (<see cref="Transport.SendResult.IsConfirmed"/> is <see langword="false"/>) — the rest of the
    /// batch is accepted and no exception is thrown.
    /// </para>
    /// </remarks>
    /// <param name="exchangeName">
    /// The exchange name, or an empty string to select the default exchange (routing by queue name).
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="exchangeName"/> is <see langword="null"/>.
    /// </exception>
    void DefaultExchange(string exchangeName);

    /// <summary>
    /// Enables opt-in guaranteed-routing mode for the send/publish path, mirroring the guaranteed-routing
    /// mode of the broker-backed transports. When enabled, a message that resolves to no declared exchange,
    /// or to an exchange with no matching binding, is surfaced to the caller as
    /// <see cref="Transport.SendResult.IsConfirmed"/> equal to <see langword="false"/> and logged, instead
    /// of being reported as confirmed while going unrouted.
    /// </summary>
    /// <remarks>
    /// <strong>Default is OFF.</strong> Without this call, a message that cannot be routed to any bound
    /// queue is dropped and reported as confirmed (at-most-once routing — loss on topology drift is
    /// possible), matching the historical broker-transport default. That loss is still observable: it is
    /// logged at Warning level with the resolved exchange and routing key, and counted in a metric, so it
    /// shows up in logs and metrics even though the caller sees the message as confirmed. Enabling this
    /// option does not by itself add retries — callers that need at-least-once delivery against routing
    /// failures must still react to a negative <see cref="Transport.SendResult.IsConfirmed"/>, subject to
    /// "Where rejections take effect" on <see cref="IInMemoryConfigurator"/>. A resolved exchange that is
    /// not declared at all is a separate, always-rejecting case regardless of this setting — see
    /// <see cref="DefaultExchange"/> and <see cref="MapExchange{T}"/>.
    /// </remarks>
    void GuaranteedRouting();

    /// <summary>
    /// Enables automatic declaration of one queue per receive endpoint at bus startup, so that
    /// <see cref="ReceiveEndpoint"/> can be called without a matching prior <see cref="ConfigureTopology"/>
    /// declaration for that queue.
    /// </summary>
    /// <remarks>
    /// <strong>Default is OFF</strong> (manual topology). When enabled, each queue named by a
    /// <see cref="ReceiveEndpoint"/> call that is not already declared is created automatically at bus
    /// startup, with no exchanges and no bindings — it is reachable only via the default exchange (an
    /// empty string passed to <see cref="DefaultExchange"/>, or a per-type mapping resolving to it).
    /// Queues are declared exclusively from the endpoint set known at startup; a queue is never created
    /// on the basis of inbound traffic, and the topology is sealed once the bus has started.
    /// </remarks>
    void AutoDeclareEndpointQueues();

    /// <summary>
    /// Sets the maximum number of messages a single in-memory queue may hold, counting messages that
    /// are queued, currently being processed, requeued via <see cref="SettlementAction"/>, or
    /// waiting to be redelivered by <see cref="EnableDefer"/>.
    /// </summary>
    /// <remarks>
    /// <para>Default is <c>1000</c>. Validated at bus startup (during <c>Build()</c>): must be greater
    /// than zero, otherwise <see cref="Exceptions.BareWireConfigurationException"/> is thrown.</para>
    /// <para>
    /// When a queue is at capacity, a send call does not throw and does not queue the message — it
    /// reports <see cref="Transport.SendResult.IsConfirmed"/> equal to <see langword="false"/> for that
    /// message. <see cref="QueueCapacity"/> multiplied by <see cref="MaxMessageSize"/> bounds the
    /// worst-case message-body footprint of a single queue (at the defaults, roughly 16 GiB); headers are
    /// not counted toward this bound. The process-wide worst case is that per-queue bound multiplied by
    /// the number of declared queues, including dead-letter queues. Lower one of the two limits when
    /// large message bodies or many queues are expected. A queue health check reports <c>Degraded</c>
    /// once occupancy reaches 90% of this capacity; a capacity below roughly 100 is rarely useful in
    /// practice.
    /// </para>
    /// </remarks>
    /// <param name="capacity">The maximum number of messages the queue may hold. Must be greater than zero.</param>
    void QueueCapacity(int capacity);

    /// <summary>
    /// Sets the maximum time a batch send call will wait, at most once per call, for space to free up
    /// in a full destination queue before giving up on the messages that still do not fit.
    /// </summary>
    /// <remarks>
    /// <para>Default is <c>100</c> milliseconds. Validated at bus startup (during <c>Build()</c>): must
    /// be greater than or equal to <see cref="TimeSpan.Zero"/>, otherwise
    /// <see cref="Exceptions.BareWireConfigurationException"/> is thrown. Passing
    /// <see cref="TimeSpan.Zero"/> means never wait — a full queue is rejected immediately.</para>
    /// <para>
    /// A send call waits only for a queue that is both full and latched (see below) and has at least
    /// one active consumer; it waits at most once per call, regardless of how many messages in the
    /// batch target that queue. A full queue with no active consumer is rejected immediately without
    /// waiting or latching. When the wait elapses without room becoming available, or when
    /// <see cref="SendTimeout"/> is <see cref="TimeSpan.Zero"/>, the still-unaccepted messages are
    /// rejected without latching. A full queue latches immediately when first rejected and stays
    /// latched — rejecting further sends without waiting — until its occupancy drops back below half of
    /// <see cref="QueueCapacity"/>. A rejected message never throws: it is reported as
    /// <see cref="Transport.SendResult.IsConfirmed"/> equal to <see langword="false"/>, and a batch send
    /// call therefore takes at most roughly <see cref="SendTimeout"/> to return. Cancelling the caller's
    /// <see cref="System.Threading.CancellationToken"/> while a send call is waiting also resolves as
    /// <see cref="Transport.SendResult.IsConfirmed"/> equal to <see langword="false"/>, not as an
    /// <see cref="OperationCanceledException"/>. Keep this value short — it bounds how long a slow
    /// consumer on one queue can stall every publisher sending to it.
    /// </para>
    /// </remarks>
    /// <param name="timeout">
    /// The maximum wait time for queue space per send call. Must be greater than or equal to
    /// <see cref="TimeSpan.Zero"/>.
    /// </param>
    void SendTimeout(TimeSpan timeout);

    /// <summary>
    /// Sets the maximum allowed size, in bytes, of a single message body.
    /// </summary>
    /// <remarks>
    /// Default is <c>16777216</c> (16 MiB). Validated at bus startup (during <c>Build()</c>): must be
    /// greater than zero, otherwise <see cref="Exceptions.BareWireConfigurationException"/> is thrown.
    /// The limit applies to the message body only. When a message in a batch exceeds this size, only
    /// that message is rejected (<see cref="Transport.SendResult.IsConfirmed"/> equal to
    /// <see langword="false"/>) — the rest of the batch is accepted, and no exception is thrown.
    /// </remarks>
    /// <param name="bytes">The maximum message body size, in bytes. Must be greater than zero.</param>
    void MaxMessageSize(int bytes);

    /// <summary>
    /// Sets the maximum number of times a message may be requeued via
    /// <see cref="SettlementAction.Requeue"/> before it is moved to a dead-letter exchange, or
    /// discarded with a log entry when none is configured.
    /// </summary>
    /// <remarks>
    /// Default is <c>20</c>. Validated at bus startup (during <c>Build()</c>): must be greater than
    /// zero, otherwise <see cref="Exceptions.BareWireConfigurationException"/> is thrown. This limit
    /// applies only to <see cref="SettlementAction.Requeue"/> — it does not apply to
    /// <see cref="SettlementAction.Defer"/>, which has its own delay-based pacing configured via
    /// <see cref="EnableDefer"/>.
    /// </remarks>
    /// <param name="maxRedeliveries">
    /// The maximum number of requeue redeliveries for a message. Must be greater than zero.
    /// </param>
    void MaxRedeliveries(int maxRedeliveries);

    /// <summary>
    /// Sets the maximum time allowed for in-memory queues to drain their remaining messages during bus
    /// shutdown, before consumers are cancelled.
    /// </summary>
    /// <remarks>
    /// Default is <c>10</c> seconds. Validated at bus startup (during <c>Build()</c>): must be greater
    /// than <see cref="TimeSpan.Zero"/>, otherwise <see cref="Exceptions.BareWireConfigurationException"/>
    /// is thrown. Set this shorter than the host's overall shutdown time limit, so the drain has a chance
    /// to complete before the process is forcibly terminated.
    /// </remarks>
    /// <param name="timeout">The maximum drain wait time at shutdown. Must be greater than <see cref="TimeSpan.Zero"/>.</param>
    void DrainTimeout(TimeSpan timeout);

    /// <summary>
    /// Enables consumers on this transport to defer a message for later redelivery via
    /// <see cref="SettlementAction.Defer"/>, using <paramref name="delay"/> as the default delay
    /// between redelivery attempts.
    /// </summary>
    /// <remarks>
    /// Default delay is <c>30</c> seconds when <paramref name="delay"/> is <see langword="null"/>. A
    /// supplied <paramref name="delay"/> must be strictly greater than <see cref="TimeSpan.Zero"/>;
    /// this is validated at bus startup (during <c>Build()</c>) and violation throws
    /// <see cref="Exceptions.BareWireConfigurationException"/> — a zero delay would let a consumer that
    /// always defers spin in a tight redelivery loop with no backpressure, since
    /// <see cref="MaxRedeliveries"/> does not apply to <see cref="SettlementAction.Defer"/>.
    /// Without a call to this method, a consumer calling <see cref="SettlementAction.Defer"/>
    /// causes a <see cref="NotSupportedException"/>. Enabling deferral on an endpoint configured with
    /// per-key consumer ordering is a configuration error, reported as
    /// <see cref="Exceptions.BareWireConfigurationException"/> at bus startup, because deferred
    /// redelivery cannot preserve per-key ordering guarantees.
    /// </remarks>
    /// <param name="delay">
    /// The default delay before a deferred message becomes eligible for redelivery, or
    /// <see langword="null"/> to use the 30-second default. When supplied, must be strictly greater than
    /// <see cref="TimeSpan.Zero"/>.
    /// </param>
    void EnableDefer(TimeSpan? delay = null);

    /// <summary>
    /// Maps a message type to an explicit routing key used by <c>PublishAsync&lt;T&gt;</c>.
    /// Required when using topic-style exchanges with pattern-based bindings (e.g. <c>order.*</c>),
    /// because the default routing key (<c>typeof(T).FullName</c>) does not match such patterns.
    /// </summary>
    /// <typeparam name="T">The message type to map. Must be a reference type.</typeparam>
    /// <param name="routingKey">
    /// The routing key to use when publishing messages of type <typeparamref name="T"/>.
    /// Must not be <see langword="null"/> or empty.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="routingKey"/> is <see langword="null"/> or empty.
    /// </exception>
    void MapRoutingKey<T>(string routingKey) where T : class;

    /// <summary>
    /// Maps a message type to a specific exchange used by <c>PublishAsync&lt;T&gt;</c>.
    /// Use this when different message types should be published to different exchanges.
    /// The specified exchange must be declared via <see cref="ConfigureTopology"/>; a missing
    /// declaration is validated at bus startup and throws
    /// <see cref="Exceptions.BareWireConfigurationException"/> (during <c>Build()</c>).
    /// </summary>
    /// <remarks>
    /// This mapping participates in the same precedence order as <see cref="DefaultExchange"/> and feeds
    /// the same per-type mapping set as <see cref="Publish{T}"/> and
    /// <see cref="ITopologyConfigurator.DeclareExchange{T}"/> (single source of truth). Calling this
    /// method multiple times for the same <typeparamref name="T"/> is allowed; the last call across any
    /// of these three shapes wins. If the resolved exchange no longer exists at the time a message is
    /// published, only that message is rejected (<see cref="Transport.SendResult.IsConfirmed"/> equal to
    /// <see langword="false"/>) — no exception is thrown at publish time.
    /// </remarks>
    /// <typeparam name="T">The message type to map. Must be a reference type.</typeparam>
    /// <param name="exchangeName">
    /// The exchange name to use when publishing messages of type <typeparamref name="T"/>.
    /// Must not be <see langword="null"/> or empty. Must match an exchange declared via
    /// <see cref="ConfigureTopology"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="exchangeName"/> is <see langword="null"/> or empty.
    /// </exception>
    /// <exception cref="Exceptions.BareWireConfigurationException">
    /// Thrown at bus startup (during <c>Build()</c>) when <paramref name="exchangeName"/> does not
    /// correspond to an exchange declared via <see cref="ConfigureTopology"/>.
    /// </exception>
    void MapExchange<T>(string exchangeName) where T : class;

    /// <summary>
    /// Configures per-type publish routing for message type <typeparamref name="T"/> on the
    /// <c>PublishAsync&lt;T&gt;</c> path as a single grouped, discoverable block, by passing an
    /// <see cref="IPublishConfigurator{T}"/> to <paramref name="configure"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Which to choose.</strong> Three complementary shapes feed the SAME per-type mapping set
    /// (single source of truth):
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>Publish&lt;T&gt;</c> (this method) — the full, grouped, discoverable send block; prefer it
    ///     when configuring the exchange and routing key for a type together.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="ITopologyConfigurator.DeclareExchange{T}"/> — the "declare + map" shortcut that
    ///     both declares the exchange and maps the type in one call.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="MapExchange{T}"/> / <see cref="MapRoutingKey{T}"/> — the low-level primitives.
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Last-call-wins.</strong> The exchange and routing key configured here participate in the
    /// same per-type mapping set as <see cref="MapExchange{T}"/> / <see cref="MapRoutingKey{T}"/>; the
    /// last call across any of these shapes wins. The exchange must be declared in the topology via
    /// <see cref="ConfigureTopology"/> — a missing declaration fails fast at bus startup (<c>Build()</c>)
    /// with <see cref="Exceptions.BareWireConfigurationException"/>, identical to
    /// <see cref="MapExchange{T}"/>.
    /// </para>
    /// <para>
    /// <strong>Scope.</strong> The <c>PublishAsync&lt;T&gt;</c> path only; point-to-point send is out of
    /// scope.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The message type to configure publish routing for. Must be a reference type.</typeparam>
    /// <param name="configure">
    /// A delegate that receives an <see cref="IPublishConfigurator{T}"/> and applies the exchange and/or
    /// routing-key mapping for type <typeparamref name="T"/>. Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    void Publish<T>(Action<IPublishConfigurator<T>> configure) where T : class;
}
