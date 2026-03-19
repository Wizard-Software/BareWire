using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;

namespace BareWire.Saga;

internal sealed class CorrelationProvider<TSaga>
    where TSaga : class, ISagaState
{
    private readonly Dictionary<Type, Func<object, Guid>> _correlations;

    internal CorrelationProvider(Dictionary<Type, Func<object, Guid>> correlations)
    {
        ArgumentNullException.ThrowIfNull(correlations);
        _correlations = correlations;
    }

    internal Guid GetCorrelationId<TEvent>(TEvent @event)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (_correlations.TryGetValue(typeof(TEvent), out var selector))
        {
            return selector(@event);
        }

        throw new BareWireSagaException(
            $"No correlation mapping for event type '{typeof(TEvent).Name}'.",
            typeof(TSaga),
            Guid.Empty);
    }
}
