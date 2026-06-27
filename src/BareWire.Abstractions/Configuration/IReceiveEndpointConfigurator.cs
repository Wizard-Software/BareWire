using BareWire.Abstractions.Serialization;

namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Configures a single receive endpoint (queue consumer) on the bus.
/// Passed to the delegate in <see cref="IBus.ConnectReceiveEndpoint"/> and to
/// static endpoint configuration during bus setup.
/// Per ADR-002, <see cref="ConfigureConsumeTopology"/> defaults to <see langword="false"/> —
/// topology must be declared and deployed explicitly.
/// </summary>
public interface IReceiveEndpointConfigurator
{
    /// <summary>
    /// Gets or sets the number of messages the broker will deliver to this endpoint
    /// before waiting for acknowledgements. Controls consumer throughput vs. fairness.
    /// </summary>
    int PrefetchCount { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of messages that can be processed concurrently
    /// by this endpoint's consumer handlers.
    /// </summary>
    int ConcurrentMessageLimit { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bus should automatically declare and bind
    /// transport topology (exchanges, queues) for this endpoint's consumers.
    /// Defaults to <see langword="false"/> per ADR-002 (manual topology).
    /// </summary>
    bool ConfigureConsumeTopology { get; set; }

    /// <summary>
    /// Gets or sets the default MIME content type assumed for messages that arrive without
    /// an explicit content-type header (e.g. <c>"application/json"</c>).
    /// <see langword="null"/> means the serializer is selected by the transport header only.
    /// </summary>
    string? DefaultContentType { get; set; }

    /// <summary>
    /// Gets or sets the raw serializer behaviour flags for messages on this endpoint.
    /// </summary>
    RawSerializerOptions RawSerializerOptions { get; set; }

    /// <summary>
    /// Gets or sets the number of times a failed message will be retried before being moved
    /// to the dead-letter queue or fault endpoint. Defaults to <c>0</c> (no retries).
    /// </summary>
    int RetryCount { get; set; }

    /// <summary>
    /// Gets or sets the delay between retry attempts. Defaults to <see cref="TimeSpan.Zero"/>
    /// (immediate retry).
    /// </summary>
    TimeSpan RetryInterval { get; set; }

    /// <summary>
    /// Registers a typed consumer <typeparamref name="TConsumer"/> that processes messages of type
    /// <typeparamref name="TMessage"/>. The consumer is resolved from the DI container per message.
    /// </summary>
    /// <typeparam name="TConsumer">
    /// The consumer implementation type. Must implement <see cref="IConsumer{TMessage}"/>.
    /// </typeparam>
    /// <typeparam name="TMessage">The message type this consumer handles. Must be a reference type.</typeparam>
    void Consumer<TConsumer, TMessage>()
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class;

    /// <summary>
    /// Registers a typed consumer <typeparamref name="TConsumer"/> that processes messages of type
    /// <typeparamref name="TMessage"/>, configured through a grouped block that declares a set of
    /// consume-time routing-key patterns. The consumer is resolved from the DI container per message.
    /// This is the consume-side ergonomic counterpart of the publish-side per-type routing configurator
    /// <see cref="IPublishConfigurator{T}"/> (with deliberately different semantics — a set of match
    /// patterns evaluated <em>client-side at dispatch</em> against the delivery's routing key, not a single
    /// produced key) and selects which of several consumers sharing a queue handles a given delivery.
    /// </summary>
    /// <remarks>
    /// This overload is purely additive — the parameterless <c>Consumer&lt;TConsumer, TMessage&gt;()</c>
    /// is unchanged. A consumer that declares no routing keys remains a catch-all over its message type
    /// (unchanged behaviour). This is a dispatcher predicate, <strong>not</strong> topology: declaring
    /// routing keys does not create or alter any queue→exchange binding (per ADR-002, manual topology).
    /// </remarks>
    /// <typeparam name="TConsumer">
    /// The consumer implementation type. Must implement <see cref="IConsumer{TMessage}"/>.
    /// </typeparam>
    /// <typeparam name="TMessage">The message type this consumer handles. Must be a reference type.</typeparam>
    /// <param name="configure">
    /// A delegate that receives an <see cref="IConsumerConfigurator{TConsumer, TMessage}"/> and declares the
    /// routing-key pattern set (and the optional type-less opt-in) for this consumer. Must not be
    /// <see langword="null"/>.
    /// </param>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <example>
    /// <code>
    /// e.Consumer&lt;RegionConsumer, TransferInitiated&gt;(c => c.RoutingKeys("transfer.eu.*", "transfer.pl.*"));
    /// </code>
    /// </example>
    void Consumer<TConsumer, TMessage>(Action<IConsumerConfigurator<TConsumer, TMessage>> configure)
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class;

    /// <summary>
    /// Registers a raw consumer <typeparamref name="T"/> that receives undeserialized byte payloads.
    /// The consumer is resolved from the DI container per message.
    /// </summary>
    /// <typeparam name="T">The raw consumer implementation type. Must implement <see cref="IRawConsumer"/>.</typeparam>
    void RawConsumer<T>() where T : class, IRawConsumer;

    /// <summary>
    /// Registers a state machine saga of type <typeparamref name="TSaga"/> on this endpoint.
    /// The saga type and its instance store are resolved from the DI container.
    /// </summary>
    /// <typeparam name="TSaga">The saga state machine type. Must be a reference type.</typeparam>
    void StateMachineSaga<TSaga>() where TSaga : class;

    /// <summary>
    /// Overrides the serializer for this endpoint. The serializer type is resolved from DI.
    /// When not set, the global bus-level serializer is used.
    /// </summary>
    /// <typeparam name="TSerializer">The serializer type to use for this endpoint.</typeparam>
    void UseSerializer<TSerializer>() where TSerializer : class, IMessageSerializer;

    /// <summary>
    /// Overrides the deserializer for this endpoint. The deserializer type is resolved from DI.
    /// When not set, the global bus-level deserializer resolver is used.
    /// </summary>
    /// <typeparam name="TDeserializer">The deserializer type to use for this endpoint.</typeparam>
    void UseDeserializer<TDeserializer>() where TDeserializer : class, IMessageDeserializer;

    /// <summary>
    /// Enables per-key consumer ordering for this endpoint, deriving the ordering key from a typed
    /// selector over the deserialized message (one-liner form). Opt-in and additive — per-key ordering
    /// is OFF by default and endpoints without an <c>OrderedBy</c> call are unaffected.
    /// </summary>
    /// <typeparam name="TMessage">The message type the selector reads. Must be a reference type.</typeparam>
    /// <param name="selector">Projects a message to its ordering key; may return <see langword="null"/> for
    /// messages that should pass through without ordering.</param>
    /// <remarks>
    /// Cross-instance caveat (M3): a CLR-property selector is safe across competing consumer instances only
    /// under <see cref="ConsumerOrderingStrategy.LocalPartitioned"/> or when the selector equals the routing
    /// key; for cross-instance transport-native ordering, prefer <see cref="OrderedByHeader"/>.
    /// Security (S1/S2): the projected key value is potential PII and MUST NOT reach any of these sinks —
    /// (1) <c>BareWireConfigurationException.OptionValue</c>; (2) the exception <c>Message</c> (which embeds
    /// <c>Supplied value: '{optionValue}'</c>); (3) logs or a per-key metric dimension. Diagnostics refer
    /// only to the selector placeholder <c>&lt;selector&gt;</c>, the strategy, or the endpoint
    /// (see <see cref="IConsumerOrderingConfigurator"/> remarks).
    /// </remarks>
    void OrderedBy<TMessage>(Func<TMessage, object?> selector) where TMessage : class;

    /// <summary>
    /// Enables per-key consumer ordering for this endpoint, deriving the ordering key from a message
    /// header (one-liner form, raw / cross-language). The header name is symmetric to the producer-side
    /// ordering-key header (ADR-025). Opt-in and additive — per-key ordering is OFF by default.
    /// </summary>
    /// <param name="headerName">The header carrying the ordering key.</param>
    /// <remarks>
    /// Security (S1/S2): pass only a constant header <em>name</em>; the resolved header <em>value</em> is
    /// potential PII and MUST NOT reach any of these sinks — (1) <c>BareWireConfigurationException.OptionValue</c>;
    /// (2) the exception <c>Message</c> (which embeds <c>Supplied value: '{optionValue}'</c>); (3) logs or a
    /// per-key metric dimension. Diagnostics refer only to the header name, strategy, or endpoint
    /// (see <see cref="IConsumerOrderingConfigurator"/> remarks).
    /// </remarks>
    void OrderedByHeader(string headerName);

    /// <summary>
    /// Enables per-key consumer ordering for this endpoint using the advanced block form, exposing
    /// <see cref="IConsumerOrderingConfigurator"/> to tune the key source, strategy, transport affinity,
    /// cross-key concurrency, and poison policy. Opt-in and additive — per-key ordering is OFF by default.
    /// </summary>
    /// <param name="configure">Configures the per-key ordering options.</param>
    /// <remarks>
    /// The one-liner <see cref="OrderedBy{TMessage}"/> and the block member
    /// <see cref="IConsumerOrderingConfigurator.By{TMessage}"/> both take the same
    /// <c>Func&lt;TMessage, object?&gt;</c>; the verb differs intentionally (one is the feature entry point,
    /// the other is a block member).
    /// Security (S1/S2): ordering-key values are potential PII and MUST NOT reach any of these sinks —
    /// (1) <c>BareWireConfigurationException.OptionValue</c>; (2) the exception <c>Message</c> (which embeds
    /// <c>Supplied value: '{optionValue}'</c>); (3) logs or a per-key metric dimension. Diagnostics refer
    /// only to the header name, the selector placeholder <c>&lt;selector&gt;</c>, the strategy, or the
    /// endpoint (see <see cref="IConsumerOrderingConfigurator"/> remarks).
    /// </remarks>
    void OrderedBy(Action<IConsumerOrderingConfigurator> configure);
}
