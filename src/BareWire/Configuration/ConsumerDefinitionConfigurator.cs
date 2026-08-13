using BareWire.Abstractions.Configuration;

namespace BareWire.Configuration;

/// <summary>
/// Core (transport-agnostic) implementation of the single-parameter façade
/// <see cref="IConsumerConfigurator{TConsumer}"/>, handed to a <c>ConsumerDefinition&lt;TConsumer&gt;</c>'s
/// <c>Configure</c> method during start-up discovery. Accumulates the same four message-agnostic settings as
/// <see cref="ConsumerConfigurator{TConsumer, TMessage}"/> and, via <see cref="Merge"/>, folds them into an
/// existing <see cref="ConsumerRegistration"/> produced by the ordinary <c>Consumer&lt;,&gt;</c> registration
/// path.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Accumulation</strong>: each <see cref="RoutingKey"/> / <see cref="RoutingKeys"/> call adds to the
/// set (order-preserving, duplicates idempotent via ordinal comparison) — mirrors
/// <see cref="ConsumerConfigurator{TConsumer, TMessage}"/>. <strong>Idempotent opt-in</strong>:
/// <see cref="AcceptUntyped"/> and <see cref="UseMassTransitEnvelope"/> each set an on/off flag.
/// </para>
/// </remarks>
/// <typeparam name="TConsumer">The consumer implementation type.</typeparam>
internal sealed class ConsumerDefinitionConfigurator<TConsumer> : IConsumerConfigurator<TConsumer>
    where TConsumer : class
{
    private readonly List<string> _routingKeys = [];
    private bool _acceptUntyped;
    private bool _useMassTransitEnvelope;
    private Action<IRetryConfigurator>? _configureRetry;

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

    /// <inheritdoc />
    public void Retry(Action<IRetryConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureRetry = configure;
    }

    /// <summary>
    /// Merges the settings accumulated from a <c>ConsumerDefinition&lt;TConsumer&gt;</c> into
    /// <paramref name="existing"/>, an already-materialized <see cref="ConsumerRegistration"/>: routing keys
    /// are unioned (existing set first, then any new pattern from this definition, deduplicated ordinally),
    /// <see cref="ConsumerRegistration.AcceptUntyped"/> and <see cref="ConsumerRegistration.UseMassTransitEnvelope"/>
    /// are OR-combined, <see cref="ConsumerRegistration.ConfigureRetry"/> is replaced when this definition
    /// composed a retry policy (via <see cref="Retry"/>) and otherwise preserved, and every other field
    /// (<see cref="ConsumerRegistration.MessageType"/>, <see cref="ConsumerRegistration.PrefetchCount"/>,
    /// <see cref="ConsumerRegistration.ConcurrentMessageLimit"/>) is preserved unchanged.
    /// </summary>
    /// <param name="existing">The registration to merge this definition's settings into.</param>
    /// <returns>A new <see cref="ConsumerRegistration"/> carrying the merged settings.</returns>
    internal ConsumerRegistration Merge(ConsumerRegistration existing)
    {
        List<string> keys = existing.RoutingKeys is { Count: > 0 } existingKeys ? [.. existingKeys] : [];
        foreach (string routingKey in _routingKeys)
        {
            if (!keys.Contains(routingKey, StringComparer.Ordinal))
            {
                keys.Add(routingKey);
            }
        }

        return existing with
        {
            RoutingKeys = keys.Count == 0 ? null : keys,
            AcceptUntyped = existing.AcceptUntyped || _acceptUntyped,
            UseMassTransitEnvelope = existing.UseMassTransitEnvelope || _useMassTransitEnvelope,
            ConfigureRetry = _configureRetry ?? existing.ConfigureRetry,
        };
    }

    private void AddDistinct(string routingKey)
    {
        if (!_routingKeys.Contains(routingKey, StringComparer.Ordinal))
        {
            _routingKeys.Add(routingKey);
        }
    }
}
