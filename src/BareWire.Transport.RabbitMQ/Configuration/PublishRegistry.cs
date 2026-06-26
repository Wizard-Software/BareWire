namespace BareWire.Transport.RabbitMQ.Configuration;

// Single config-time source of truth for per-type publish routing maps.
// A single instance is created by RabbitMqConfigurator and SHARED BY REFERENCE with the
// lazily-created RabbitMqTopologyConfigurator, so every write path — MapExchange<T> /
// MapRoutingKey<T> (low-level primitives), DeclareExchange<T> (declare + map shortcut),
// and Publish<T> (grouped block) — accumulates into ONE map set with no parallel dictionary
// and no merge step in Build(). Observable ordering is last-call-wins by source order.
internal sealed class PublishRegistry
{
    // Type→exchange map consumed by the PublishAsync<T> path (ExchangeResolver).
    public Dictionary<Type, string> ExchangeMappings { get; } = [];

    // Type→routing-key map consumed by the PublishAsync<T> path (RoutingKeyResolver);
    // absence falls back to typeof(T).FullName at resolve time.
    public Dictionary<Type, string> RoutingKeyMappings { get; } = [];

    // Config-time-only diagnostic accumulator. Every divergent overwrite (the same type T receiving
    // a DIFFERENT exchange or routing key from a second registration path) is recorded here so the
    // transport can emit a DEFAULT-ON warning at bus startup. Last-call-wins still applies (no
    // exception, non-breaking) — the divergence is merely made LOUD. Idempotent re-writes of the
    // SAME value record nothing. Zero runtime cost: this list never leaves config time.
    private readonly List<PublishRoutingDivergence> _divergences = [];

    public IReadOnlyList<PublishRoutingDivergence> Divergences => _divergences;

    public void MapExchange(Type messageType, string exchangeName)
    {
        RecordDivergenceIfAny(PublishRoutingDimension.Exchange, messageType, ExchangeMappings, exchangeName);
        ExchangeMappings[messageType] = exchangeName;
    }

    public void MapRoutingKey(Type messageType, string routingKey)
    {
        RecordDivergenceIfAny(PublishRoutingDimension.RoutingKey, messageType, RoutingKeyMappings, routingKey);
        RoutingKeyMappings[messageType] = routingKey;
    }

    private void RecordDivergenceIfAny(
        PublishRoutingDimension dimension,
        Type messageType,
        Dictionary<Type, string> map,
        string newValue)
    {
        // Loud only when the key already holds a DIFFERENT value. Idempotent re-write → silent.
        if (map.TryGetValue(messageType, out string? existing)
            && !string.Equals(existing, newValue, StringComparison.Ordinal))
        {
            _divergences.Add(new PublishRoutingDivergence(dimension, messageType, existing, newValue));
        }
    }
}
