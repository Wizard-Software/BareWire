using BareWire.Abstractions.Routing;

namespace BareWire.Transport.RabbitMQ.Internal;

internal sealed class RoutingKeyResolver : IRoutingKeyResolver
{
    private readonly IReadOnlyDictionary<Type, string> _mappings;

    internal RoutingKeyResolver(IReadOnlyDictionary<Type, string>? mappings = null)
    {
        _mappings = mappings ?? new Dictionary<Type, string>();
    }

    public string Resolve<T>() where T : class =>
        _mappings.TryGetValue(typeof(T), out string? key)
            ? key
            : typeof(T).FullName ?? typeof(T).Name;
}
