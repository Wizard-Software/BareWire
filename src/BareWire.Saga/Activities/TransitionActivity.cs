using BareWire.Abstractions.Saga;

namespace BareWire.Saga.Activities;

internal sealed class TransitionActivity<TSaga, TEvent> : IActivityStep<TSaga, TEvent>
    where TSaga : class, ISagaState
    where TEvent : class
{
    private readonly string _stateName;

    internal TransitionActivity(string stateName)
    {
        ArgumentNullException.ThrowIfNull(stateName);
        _stateName = stateName;
    }

    public Task ExecuteAsync(BehaviorContext<TSaga, TEvent> context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.TargetState = _stateName;
        return Task.CompletedTask;
    }
}
