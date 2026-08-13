using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;

namespace BareWire.Configuration;

/// <summary>
/// Core (transport-agnostic) implementation of <see cref="IConsumerConfigurator{TConsumer, TMessage}"/>,
/// driving the grouped <c>Consumer&lt;TConsumer, TMessage&gt;(Action&lt;...&gt;)</c> overload on
/// <see cref="ReceiveEndpointConfiguration"/>. Accumulates the consumer's set of AMQP topic patterns and the
/// secure-by-default <see cref="AcceptUntyped"/> and <see cref="UseMassTransitEnvelope"/> opt-ins, then
/// materializes them into an immutable <see cref="ConsumerRegistration"/> via <see cref="Build"/>.
/// </summary>
/// <remarks>
/// <para>
/// The configurator is <strong>per-project</strong>: core (<c>BareWire</c>) and transport
/// (<c>BareWire.Transport.RabbitMQ</c>) internals are not shared, so each implementation owns a separate
/// <c>internal sealed ConsumerConfigurator&lt;,&gt;</c> with identical semantics.
/// </para>
/// <para>
/// <strong>Accumulation</strong>: each <see cref="RoutingKey"/> / <see cref="RoutingKeys"/> call adds to the
/// set (order-preserving, duplicates idempotent via ordinal comparison) — a consumer may listen on many keys.
/// <strong>Idempotent opt-in</strong>: <see cref="AcceptUntyped"/> and <see cref="UseMassTransitEnvelope"/>
/// each set an on/off flag (calling either more than once has the same effect as calling it once).
/// </para>
/// </remarks>
/// <typeparam name="TConsumer">The consumer implementation type.</typeparam>
/// <typeparam name="TMessage">The message type the consumer handles.</typeparam>
internal sealed class ConsumerConfigurator<TConsumer, TMessage> : IConsumerConfigurator<TConsumer, TMessage>
    where TConsumer : class, IConsumer<TMessage>
    where TMessage : class
{
    private readonly List<string> _routingKeys = [];
    private bool _acceptUntyped;
    private bool _useMassTransitEnvelope;
    private Action<IRetryConfigurator>? _configureRetry;
    private int? _prefetchCount;
    private int? _concurrentMessageLimit;

    /// <inheritdoc />
    public void RoutingKey(string routingKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(routingKey);
        AddDistinct(routingKey);
    }

    /// <inheritdoc />
    public void RoutingKeys(params string[] routingKeys)
    {
        ArgumentNullException.ThrowIfNull(routingKeys);
        foreach (string routingKey in routingKeys)
        {
            ArgumentException.ThrowIfNullOrEmpty(routingKey);
            AddDistinct(routingKey);
        }
    }

    /// <inheritdoc />
    public void AcceptUntyped() => _acceptUntyped = true;

    /// <inheritdoc />
    public void UseMassTransitEnvelope() => _useMassTransitEnvelope = true;

    /// <summary>
    /// Captures deferred configuration of this consumer's retry policy as a delegate over the public
    /// <see cref="IRetryConfigurator"/> fluent contract. The delegate is stored verbatim and flows into
    /// <see cref="ConsumerRegistration.ConfigureRetry"/> unchanged; materialization to a concrete retry
    /// policy happens later in the core. Last call wins (a scalar knob, unlike the accumulating routing-key
    /// set or the idempotent on/off flags).
    /// </summary>
    /// <param name="configure">The retry-configuration delegate. Must not be <see langword="null"/>.</param>
    public void Retry(Action<IRetryConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureRetry = configure;
    }

    /// <summary>
    /// Sets the endpoint-level prefetch limit — the maximum number of unacknowledged messages the broker may
    /// deliver to this consumer before waiting for settlement. Bounds validation happens here, at
    /// materialization time (not on <see cref="ConsumerRegistration"/>). Last call wins.
    /// </summary>
    /// <param name="prefetchCount">The prefetch limit. Must be positive.</param>
    internal void PrefetchCount(int prefetchCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prefetchCount);
        _prefetchCount = prefetchCount;
    }

    /// <summary>
    /// Sets the endpoint-level concurrency limit — the maximum number of messages this consumer may process
    /// in parallel. Bounds validation happens here, at materialization time (not on
    /// <see cref="ConsumerRegistration"/>). Last call wins.
    /// </summary>
    /// <param name="concurrentMessageLimit">The concurrency limit. Must be positive.</param>
    internal void ConcurrentMessageLimit(int concurrentMessageLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrentMessageLimit);
        _concurrentMessageLimit = concurrentMessageLimit;
    }

    /// <summary>
    /// Materializes the accumulated state into an immutable <see cref="ConsumerRegistration"/>. An empty
    /// routing-key set materializes as <see langword="null"/> (catch-all selected by message type alone).
    /// The retry carrier and the endpoint-level prefetch/concurrency knobs flow through unchanged, alongside
    /// the routing keys and the <see cref="AcceptUntyped"/> / <see cref="UseMassTransitEnvelope"/> flags.
    /// </summary>
    /// <returns>The registration for this consumer, ready for dispatch.</returns>
    internal ConsumerRegistration Build() =>
        new(
            typeof(TConsumer),
            typeof(TMessage),
            _routingKeys.Count == 0 ? null : _routingKeys.ToArray(),
            _acceptUntyped,
            _useMassTransitEnvelope,
            _configureRetry,
            _prefetchCount,
            _concurrentMessageLimit);

    private void AddDistinct(string routingKey)
    {
        if (!_routingKeys.Contains(routingKey, StringComparer.Ordinal))
        {
            _routingKeys.Add(routingKey);
        }
    }
}
