using BareWire.Abstractions.Configuration;

namespace BareWire.Transport.RabbitMQ.Configuration;

// Grouped per-type publish-routing block handed to the Publish<T> delegate. Both methods
// write THROUGH to the shared PublishRegistry (single source of truth) — they do not own a
// parallel dictionary — so settings made here are last-call-wins-merged with MapExchange<T> /
// MapRoutingKey<T> / DeclareExchange<T>. Methods are void per the house configurator convention.
internal sealed class PublishConfigurator<T> : IPublishConfigurator<T>
    where T : class
{
    private readonly PublishRegistry _registry;

    internal PublishConfigurator(PublishRegistry registry) => _registry = registry;

    public void Exchange(string exchangeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(exchangeName);
        _registry.MapExchange(typeof(T), exchangeName);
    }

    public void RoutingKey(string routingKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(routingKey);
        _registry.MapRoutingKey(typeof(T), routingKey);
    }
}
