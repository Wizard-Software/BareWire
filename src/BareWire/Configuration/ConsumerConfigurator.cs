using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;

namespace BareWire.Configuration;

/// <summary>
/// Core (transport-agnostic) implementation of <see cref="IConsumerConfigurator{TConsumer, TMessage}"/>,
/// driving the grouped <c>Consumer&lt;TConsumer, TMessage&gt;(Action&lt;...&gt;)</c> overload on
/// <see cref="ReceiveEndpointConfiguration"/>. Accumulates the consumer's set of AMQP topic patterns and the
/// secure-by-default <see cref="AcceptUntyped"/> opt-in, then materializes both into an immutable
/// <see cref="ConsumerRegistration"/> via <see cref="Build"/>.
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
/// <strong>Idempotent opt-in</strong>: <see cref="AcceptUntyped"/> sets an on/off flag.
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
            _acceptUntyped);

    private void AddDistinct(string routingKey)
    {
        if (!_routingKeys.Contains(routingKey, StringComparer.Ordinal))
        {
            _routingKeys.Add(routingKey);
        }
    }
}
