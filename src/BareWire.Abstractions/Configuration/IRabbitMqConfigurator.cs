using BareWire.Abstractions.Headers;
using BareWire.Abstractions.Topology;

namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Provides a typed fluent API for configuring the RabbitMQ transport layer on a BareWire bus.
/// Obtained via <see cref="IBusConfigurator.UseRabbitMQ"/> during application startup.
/// </summary>
public interface IRabbitMqConfigurator
{
    /// <summary>
    /// Configures the RabbitMQ broker host connection.
    /// Must be called at least once before the bus is started.
    /// </summary>
    /// <param name="uri">
    /// The RabbitMQ connection URI. Must use the <c>amqp://</c> or <c>amqps://</c> scheme
    /// (e.g. <c>amqp://guest:guest@localhost:5672/</c>).
    /// Must not be <see langword="null"/> or empty.
    /// </param>
    /// <param name="configure">
    /// An optional delegate to configure host-level settings such as credentials and TLS.
    /// When <see langword="null"/>, credentials embedded in the URI are used.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="uri"/> is <see langword="null"/> or empty.
    /// </exception>
    void Host(string uri, Action<IHostConfigurator>? configure = null);

    /// <summary>
    /// Configures the AMQP topology (exchanges, queues, bindings) that will be deployed
    /// to the broker when <c>IBusControl.DeployTopologyAsync</c> is called.
    /// Topology must be declared and deployed explicitly (manual topology — nothing is auto-declared).
    /// </summary>
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
    /// header is present on the outbound message.
    /// This must match an exchange declared via <see cref="ConfigureTopology"/> (manual topology).
    /// </summary>
    /// <param name="exchangeName">
    /// The exchange name. Must not be <see langword="null"/> or empty.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="exchangeName"/> is <see langword="null"/> or empty.
    /// </exception>
    void DefaultExchange(string exchangeName);

    /// <summary>
    /// Enables opt-in guaranteed-routing mode for the send/publish path. When enabled, outbound messages
    /// are published with the AMQP <c>mandatory</c> flag set, so a publication the broker accepts but
    /// cannot route to any queue — caused by topology drift, a missing binding/queue, or a wrong routing
    /// key — is surfaced as <c>SendResult.IsConfirmed = false</c> (and logged by the transport), instead
    /// of the publisher confirm reporting success for a message no queue ever received.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Default is OFF.</strong> Without this call the send path is bit-identical to the historical
    /// behavior: messages are published with <c>mandatory: false</c>, and a publication the broker accepts
    /// but cannot route is reported as confirmed (at-most-once routing — silent loss on topology drift is
    /// possible). Publisher confirms guarantee only that the broker <em>accepted</em> the publication, not
    /// that it was <em>routed</em> to a queue; this option closes that gap for queued sends.
    /// </para>
    /// <para>
    /// <strong>Where it takes effect.</strong> The fail-closed behavior is realized by components that
    /// inspect the returned <c>SendResult</c> — chiefly the transactional outbox dispatcher, which treats
    /// <c>IsConfirmed == false</c> as non-delivery and retries (the outbox row stays claimed rather than
    /// being marked delivered). The direct <c>IBus.PublishAsync</c> / <c>ISendEndpoint.SendAsync</c> path
    /// is fire-and-forget: the caller does not receive a <c>SendResult</c>, and the background publisher
    /// does not redeliver on a negative confirm. On that path this option turns a silent drop into an
    /// <em>observable</em> one (the transport logs a warning for the unroutable return) but does not by
    /// itself make direct publishing at-least-once. For at-least-once delivery against topology drift,
    /// publish through the outbox with this option enabled.
    /// </para>
    /// <para>
    /// <strong>Scope.</strong> Transport-wide toggle for the send/publish path. It does not affect the
    /// request/response path (which has its own strict mode) or durable park (which always publishes
    /// mandatory). With the option OFF an unroutable outbox publication is reported as delivered and the
    /// record is removed despite no consumer ever receiving it.
    /// </para>
    /// <para>
    /// <strong>Performance.</strong> Routable publications are unaffected: the publish channel already
    /// awaits a per-message publisher confirm, so the <c>mandatory</c> flag adds no extra round-trip.
    /// Only an unroutable publication incurs a returned-message exception (an exceptional, misconfiguration
    /// path). There is no per-message allocation on the hot path.
    /// </para>
    /// </remarks>
    void GuaranteedRouting();

    /// <summary>
    /// Configures the mapping between BareWire canonical header names and RabbitMQ
    /// transport-specific header names. Use this to integrate with services that use
    /// non-standard or legacy header conventions.
    /// </summary>
    /// <param name="configure">
    /// A delegate that receives an <see cref="IHeaderMappingConfigurator"/> and applies
    /// the desired mappings.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    void ConfigureHeaderMapping(Action<IHeaderMappingConfigurator> configure);

    /// <summary>
    /// Maps a message type to an explicit AMQP routing key used by <c>PublishAsync&lt;T&gt;</c>.
    /// Required when using topic exchanges with pattern-based bindings (e.g. <c>order.*</c>),
    /// because the default routing key (<c>typeof(T).FullName</c>) does not match such patterns.
    /// </summary>
    /// <typeparam name="T">The message type to map.</typeparam>
    /// <param name="routingKey">
    /// The routing key to use when publishing messages of type <typeparamref name="T"/>.
    /// Must not be <see langword="null"/> or empty.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="routingKey"/> is <see langword="null"/> or empty.
    /// </exception>
    void MapRoutingKey<T>(string routingKey) where T : class;

    /// <summary>
    /// Maps a message type to a specific AMQP exchange used by <c>PublishAsync&lt;T&gt;</c>.
    /// Use this when different message types should be published to different exchanges.
    /// The specified exchange must be declared via <see cref="ConfigureTopology"/>; validation
    /// is performed at bus startup and throws <see cref="Exceptions.BareWireConfigurationException"/>
    /// when the exchange is missing from the declared topology.
    /// </summary>
    /// <remarks>
    /// This mapping participates in the following precedence order (highest to lowest):
    /// <list type="number">
    ///   <item><description>Explicit <c>BW-Exchange</c> header passed by the caller to <c>PublishAsync</c>.</description></item>
    ///   <item><description>Type→exchange mapping registered via this method.</description></item>
    ///   <item><description>Global <c>DefaultExchange</c> configured on the transport.</description></item>
    ///   <item><description>No exchange resolved → <see cref="Exceptions.BareWireConfigurationException"/> at publish time.</description></item>
    /// </list>
    /// Calling this method multiple times for the same <typeparamref name="T"/> is allowed; the last
    /// call wins. Note that <see cref="DefaultExchange"/> is not validated against declared topology
    /// (this asymmetry is intentional and out of scope for this release).
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
    /// <see cref="IPublishConfigurator{T}"/> to <paramref name="configure"/>. This is the full,
    /// grouped form of the send configuration (parity with <c>IPublishEndpoint</c> / <c>PublishAsync&lt;T&gt;</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Which to choose.</strong> Three complementary shapes feed the SAME per-type mapping set
    /// (single source of truth):
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>Publish&lt;T&gt;</c> (this method) — the full, grouped, discoverable send block; prefer it when
    ///     configuring the exchange and routing key for a type together.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="ITopologyConfigurator.DeclareExchange{T}"/> — the "declare + map" shortcut that both
    ///     declares the exchange and maps the type in one call.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="MapExchange{T}"/> / <see cref="MapRoutingKey{T}"/> — the low-level primitives.
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Last-call-wins.</strong> The exchange and routing key configured here participate in the same
    /// per-type mapping set as <see cref="MapExchange{T}"/> / <see cref="MapRoutingKey{T}"/>; the last call
    /// across any of these shapes wins. The exchange must be declared in the topology via
    /// <see cref="ConfigureTopology"/> — a missing declaration fails fast at bus startup (<c>Build()</c>) with
    /// <see cref="Exceptions.BareWireConfigurationException"/>, identical to <see cref="MapExchange{T}"/>.
    /// </para>
    /// <para>
    /// <strong>Scope.</strong> The <c>PublishAsync&lt;T&gt;</c> path only; point-to-point <c>SendAsync&lt;T&gt;</c>
    /// is out of scope. The method is named <c>Publish</c> (not <c>Send</c>) for parity with <c>IPublishEndpoint</c>
    /// and <see cref="PublishRequest{T}()"/>.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The message type to configure publish routing for. Must be a reference type.</typeparam>
    /// <param name="configure">
    /// A delegate that receives an <see cref="IPublishConfigurator{T}"/> and applies the exchange and/or
    /// routing-key mapping for type <typeparamref name="T"/>. Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    void Publish<T>(Action<IPublishConfigurator<T>> configure) where T : class;

    /// <summary>
    /// Enables publish-style request/response for message type <typeparamref name="T"/> using
    /// the default <c>Namespace:TypeName</c> exchange name formatter, with <c>Strict</c> and
    /// <c>AutoDeclare</c> both set to <see langword="false"/>.
    /// This mode is OFF by default — without an explicit <c>PublishRequest&lt;T&gt;</c> call
    /// the publish path for <typeparamref name="T"/> is bit-identical to a plain <c>PublishAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-type exchange name is derived by the default <c>Namespace:TypeName</c> formatter
    /// (e.g., <c>MyApp.Messages:OrderRequest</c>). To override the name or enable strict / auto-declare
    /// behavior, use the <see cref="PublishRequest{T}(System.Action{IPublishRequestOptions})"/> overload.
    /// </para>
    /// <para>
    /// <strong>Last-call-wins.</strong> Calling this method (or its overload) multiple times for the
    /// same <typeparamref name="T"/> is allowed; the last call takes effect, discarding any previous
    /// registration for that type (same precedence rule as <see cref="MapExchange{T}"/>).
    /// </para>
    /// <para>
    /// <strong>Topology requirement.</strong> The resolved per-type fanout exchange must be declared in
    /// the topology via <see cref="ConfigureTopology"/>. Fail-fast validation is performed at bus startup
    /// (<c>Build()</c>) and throws <see cref="Exceptions.BareWireConfigurationException"/> when the
    /// exchange is absent from the declared topology.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The request message type to enable publish-style routing for. Must be a reference type.</typeparam>
    void PublishRequest<T>() where T : class;

    /// <summary>
    /// Enables publish-style request/response for message type <typeparamref name="T"/> and applies
    /// additional options via the <paramref name="configure"/> delegate.
    /// This mode is OFF by default — without an explicit <c>PublishRequest&lt;T&gt;</c> call
    /// the publish path for <typeparamref name="T"/> is bit-identical to a plain <c>PublishAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-type exchange name defaults to the <c>Namespace:TypeName</c> formatter
    /// (e.g., <c>MyApp.Messages:OrderRequest</c>). Set <see cref="IPublishRequestOptions.ExchangeName"/>
    /// in the <paramref name="configure"/> delegate to override it.
    /// </para>
    /// <para>
    /// <strong>Last-call-wins.</strong> Calling this method (or its parameterless overload) multiple times
    /// for the same <typeparamref name="T"/> is allowed; the last call takes effect, discarding any previous
    /// registration for that type (same precedence rule as <see cref="MapExchange{T}"/>).
    /// </para>
    /// <para>
    /// <strong>Topology requirement.</strong> The resolved per-type fanout exchange must be declared in
    /// the topology via <see cref="ConfigureTopology"/>. Fail-fast validation is performed at bus startup
    /// (<c>Build()</c>) and throws <see cref="Exceptions.BareWireConfigurationException"/> when the
    /// exchange is absent from the declared topology (unless <see cref="IPublishRequestOptions.AutoDeclare"/>
    /// is <see langword="true"/>, in which case the exchange is declared automatically during topology
    /// deployment).
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The request message type to enable publish-style routing for. Must be a reference type.</typeparam>
    /// <param name="configure">
    /// A delegate that receives an <see cref="IPublishRequestOptions"/> and applies the desired
    /// options (exchange name override, strict mode, auto-declare). Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    void PublishRequest<T>(Action<IPublishRequestOptions> configure) where T : class;
}
