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

    public void MapExchange(Type messageType, string exchangeName) =>
        ExchangeMappings[messageType] = exchangeName;

    public void MapRoutingKey(Type messageType, string routingKey) =>
        RoutingKeyMappings[messageType] = routingKey;
}
