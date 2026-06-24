namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Configures per-key consumer ordering for a receive endpoint (the advanced block overload of
/// <see cref="IReceiveEndpointConfigurator.OrderedBy(System.Action{IConsumerOrderingConfigurator})"/>).
/// The implementation is internal; only this interface is public (house convention: MassTransit-style
/// naming, internal implementation). The surface is opt-in and additive — endpoints without an
/// <c>OrderedBy</c> call are unaffected, and per-key ordering is OFF by default.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Key cardinality and stability.</strong> The ordering key partitions the message stream; choose
/// a key with bounded cardinality and stable membership. Unbounded key cardinality grows in-process
/// per-key state and high key churn defeats transport-native affinity.
/// </para>
/// <para>
/// <strong>Cross-instance caveat (M3).</strong> A typed selector (<see cref="By{TMessage}"/>) reads a CLR
/// property after deserialization, which may differ from the transport routing key — it is safe across
/// competing consumer instances only under <see cref="ConsumerOrderingStrategy.LocalPartitioned"/> or when
/// the selector equals the routing key. For <see cref="ConsumerOrderingStrategy.TransportNative"/> /
/// <see cref="ConsumerOrderingStrategy.Auto"/> cross-instance, prefer <see cref="ByHeader"/> (a name
/// symmetric to the producer side).
/// </para>
/// <para>
/// <strong>Security — ordering-key value is potential PII (S1/S2).</strong> The evaluated ordering-key
/// value MUST NEVER appear in any of these sinks: (1) <c>BareWireConfigurationException.OptionValue</c>;
/// (2) the exception <c>Message</c> (which embeds <c>Supplied value: '{optionValue}'</c>); (3) logs or a
/// per-key metric dimension. Fail-fast text and diagnostics refer only to the header name, a constant
/// selector placeholder (e.g. <c>&lt;selector&gt;</c>), the strategy, or the endpoint; gap logs and
/// metrics use a hashed/opaque key token, never the raw value.
/// </para>
/// </remarks>
public interface IConsumerOrderingConfigurator
{
    /// <summary>
    /// Derives the ordering key from a message header (raw / cross-language). The header name is symmetric
    /// to the producer-side ordering-key header (ADR-025).
    /// </summary>
    /// <param name="headerName">The header carrying the ordering key.</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    /// <remarks>
    /// Security: pass only a constant header <em>name</em> here; the resolved header <em>value</em> is
    /// potential PII and MUST NOT be logged or embedded in exception text (see the type-level remarks).
    /// </remarks>
    IConsumerOrderingConfigurator ByHeader(string headerName);

    /// <summary>
    /// Derives the ordering key from a typed selector over the deserialized message.
    /// </summary>
    /// <typeparam name="TMessage">The message type the selector reads. Must be a reference type.</typeparam>
    /// <param name="selector">Projects a message to its ordering key; may return <see langword="null"/> for
    /// messages that should pass through without ordering (heterogeneous streams are allowed).</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    /// <remarks>
    /// Cross-instance caveat (M3): a CLR-property selector is safe across instances only under
    /// <see cref="ConsumerOrderingStrategy.LocalPartitioned"/> or when the selector equals the routing key.
    /// Security: the projected key value is potential PII and MUST NOT be logged or embedded in exception
    /// text (see the type-level remarks).
    /// </remarks>
    IConsumerOrderingConfigurator By<TMessage>(Func<TMessage, object?> selector) where TMessage : class;

    /// <summary>
    /// Uses the auto-stamped correlation-id as the ordering key (fallback in the key-source chain).
    /// </summary>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    /// <remarks>
    /// Cross-instance caveat (M3): the correlation-id fallback is limited to
    /// <see cref="ConsumerOrderingStrategy.LocalPartitioned"/> (or when the correlation-id equals the
    /// routing key); under <see cref="ConsumerOrderingStrategy.TransportNative"/> cross-instance the
    /// correlation-id is not the routing key, so it does not silently become the ordering key.
    /// </remarks>
    IConsumerOrderingConfigurator ByCorrelationId();

    /// <summary>
    /// Sets the cross-key parallelism (number of fixed lanes) of the local partitioned-dispatch layer.
    /// </summary>
    /// <param name="degree">The cross-key degree of parallelism (lane count).</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    IConsumerOrderingConfigurator Concurrency(int degree);

    /// <summary>
    /// Selects the ordering strategy. Default <see cref="ConsumerOrderingStrategy.Auto"/>.
    /// </summary>
    /// <param name="strategy">The ordering strategy to apply.</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    IConsumerOrderingConfigurator Strategy(ConsumerOrderingStrategy strategy);

    /// <summary>
    /// Declares the transport-native affinity intent for this endpoint (read at startup; no broker
    /// round-trip). Drives reachable fail-fast for RabbitMQ, which exposes no introspectable capability
    /// flag. Default <see cref="Configuration.TransportAffinity.None"/>.
    /// </summary>
    /// <param name="affinity">The declared transport-native affinity intent.</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    IConsumerOrderingConfigurator TransportAffinity(TransportAffinity affinity);

    /// <summary>
    /// Maximum delivery attempts before a poison message is parked/dead-lettered and the key released
    /// (anti-starvation contract). Default <c>0</c> = disabled (reuse the endpoint <c>RetryCount</c>).
    /// </summary>
    /// <param name="attempts">The per-key maximum delivery attempts.</param>
    /// <returns>The same configurator instance for fluent chaining.</returns>
    IConsumerOrderingConfigurator MaxDeliveryAttempts(int attempts);
}
