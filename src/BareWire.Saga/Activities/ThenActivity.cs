using BareWire.Abstractions.Saga;

namespace BareWire.Saga.Activities;

internal sealed class ThenActivity<TSaga, TEvent> : IActivityStep<TSaga, TEvent>
    where TSaga : class, ISagaState
    where TEvent : class
{
    private readonly Func<TSaga, TEvent, Task> _action;

    internal ThenActivity(Func<TSaga, TEvent, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _action = action;
    }

    public async Task ExecuteAsync(BehaviorContext<TSaga, TEvent> context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await _action(context.Saga, context.Event).ConfigureAwait(false);
    }
}
