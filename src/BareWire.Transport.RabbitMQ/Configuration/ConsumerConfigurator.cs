using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;

namespace BareWire.Transport.RabbitMQ.Configuration;

/// <summary>
/// RabbitMQ-transport implementation of <see cref="IConsumerConfigurator{TConsumer, TMessage}"/>,
/// driving the grouped <c>Consumer&lt;TConsumer, TMessage&gt;(Action&lt;...&gt;)</c> overload on
/// <see cref="RabbitMqEndpointConfiguration"/>. Accumulates the consumer's set of AMQP topic patterns, the
/// secure-by-default <see cref="AcceptUntyped"/> opt-in, and the per-consumer
/// <see cref="UseMassTransitEnvelope"/> envelope opt-in, then materializes them into an immutable
/// <see cref="ConsumerRegistration"/> via <see cref="Build"/>.
/// </summary>
/// <remarks>
/// <para>
/// The configurator is <strong>per-project</strong>: transport (<c>BareWire.Transport.RabbitMQ</c>) and core
/// (<c>BareWire</c>) internals are not shared, so each implementation owns a separate
/// <c>internal sealed ConsumerConfigurator&lt;,&gt;</c> with identical semantics. This transport copy depends
/// only on <c>BareWire.Abstractions</c> (the package dependency rule forbids referencing core).
/// </para>
/// <para>
/// <strong>Accumulation</strong>: each <see cref="RoutingKey"/> / <see cref="RoutingKeys"/> call adds to the
/// set (order-preserving, duplicates idempotent via ordinal comparison) — a consumer may listen on many keys.
/// <strong>Idempotent opt-in</strong>: <see cref="AcceptUntyped"/> and <see cref="UseMassTransitEnvelope"/>
/// each set an independent on/off flag (calling either more than once has the same effect as calling it once).
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
            _useMassTransitEnvelope);

    private void AddDistinct(string routingKey)
    {
        if (!_routingKeys.Contains(routingKey, StringComparer.Ordinal))
        {
            _routingKeys.Add(routingKey);
        }
    }
}
