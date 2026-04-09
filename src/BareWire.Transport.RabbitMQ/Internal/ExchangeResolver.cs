using BareWire.Abstractions.Routing;

namespace BareWire.Transport.RabbitMQ.Internal;

internal sealed class ExchangeResolver : IExchangeResolver
{
    private readonly IReadOnlyDictionary<Type, string> _mappings;

    internal ExchangeResolver(IReadOnlyDictionary<Type, string>? mappings = null)
    {
        _mappings = mappings ?? new Dictionary<Type, string>();
    }

    public string? Resolve<T>() where T : class =>
        _mappings.TryGetValue(typeof(T), out string? exchange) ? exchange : null;
}
