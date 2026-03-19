using BareWire.Abstractions.Saga;

namespace BareWire.Saga.Activities;

internal sealed class FinalizeActivity<TSaga, TEvent> : IActivityStep<TSaga, TEvent>
    where TSaga : class, ISagaState
    where TEvent : class
{
    public Task ExecuteAsync(BehaviorContext<TSaga, TEvent> context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ShouldFinalize = true;
        return Task.CompletedTask;
    }
}
