using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;

namespace BareWire.Saga;

internal sealed class SagaEventRouter
{
    private readonly Dictionary<Type, Func<object, ConsumeContext, CancellationToken, Task>> _routes = [];

    internal void Register<TSaga, TEvent>(StateMachineExecutor<TSaga> executor)
        where TSaga : class, ISagaState, new()
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(executor);
        _routes[typeof(TEvent)] = (evt, ctx, ct) =>
            executor.ProcessEventAsync((TEvent)evt, ctx, ct);
    }

    internal async Task RouteAsync<TEvent>(
        TEvent @event,
        ConsumeContext context,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        if (!_routes.TryGetValue(typeof(TEvent), out var handler))
        {
            throw new BareWireSagaException(
                $"No saga registered to handle event type '{typeof(TEvent).Name}'.",
                typeof(object),
                Guid.Empty);
        }

        await handler(@event, context, cancellationToken).ConfigureAwait(false);
    }
}
