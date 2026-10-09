using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;

namespace BareWire.Transport.Kafka.Configuration;

/// <summary>
/// Kafka-transport implementation of <see cref="IConsumerConfigurator{TConsumer, TMessage}"/>,
/// driving the grouped <c>Consumer&lt;TConsumer, TMessage&gt;(Action&lt;...&gt;)</c> overload on
/// <see cref="KafkaEndpointConfiguration"/>. Accumulates the consumer's set of routing-key match
/// patterns, the secure-by-default <see cref="AcceptUntyped"/> opt-in, and the per-consumer
/// <see cref="UseMassTransitEnvelope"/> envelope opt-in, plus the endpoint-level definition settings —
/// the retry carrier (<see cref="Retry"/>) and the <see cref="PrefetchCount"/> / <see cref="ConcurrentMessageLimit"/>
/// knobs — then materializes them into an immutable <see cref="ConsumerRegistration"/> via <see cref="Build"/>.
/// </summary>
/// <remarks>
/// The configurator is <strong>per-project</strong>: transport (<c>BareWire.Transport.Kafka</c>) and
/// core (<c>BareWire</c>) internals are not shared, so each implementation owns a separate
/// <c>internal sealed</c> consumer configurator with identical semantics. This transport copy depends
/// only on <c>BareWire.Abstractions</c> (the package dependency rule forbids referencing core or other
/// transports).
/// </remarks>
/// <typeparam name="TConsumer">The consumer implementation type.</typeparam>
/// <typeparam name="TMessage">The message type the consumer handles.</typeparam>
internal sealed class KafkaConsumerConfigurator<TConsumer, TMessage> : IConsumerConfigurator<TConsumer, TMessage>
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
    /// <see cref="IRetryConfigurator"/> fluent contract. Last call wins (a scalar knob, unlike the
    /// accumulating routing-key set or the idempotent on/off flags).
    /// </summary>
    /// <param name="configure">The retry-configuration delegate. Must not be <see langword="null"/>.</param>
    public void Retry(Action<IRetryConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureRetry = configure;
    }

    /// <summary>
    /// Sets the endpoint-level prefetch limit. Bounds validation happens here, at materialization time
    /// (not on <see cref="ConsumerRegistration"/>). Last call wins.
    /// </summary>
    /// <param name="prefetchCount">The prefetch limit. Must be positive.</param>
    internal void PrefetchCount(int prefetchCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prefetchCount);
        _prefetchCount = prefetchCount;
    }

    /// <summary>
    /// Sets the endpoint-level concurrency limit. Bounds validation happens here, at materialization
    /// time (not on <see cref="ConsumerRegistration"/>). Last call wins.
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
