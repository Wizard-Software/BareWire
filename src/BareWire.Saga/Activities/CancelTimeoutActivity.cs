using BareWire.Abstractions.Saga;

namespace BareWire.Saga.Activities;

internal sealed class CancelTimeoutActivity<TSaga, TEvent, TTimeout> : IActivityStep<TSaga, TEvent>
    where TSaga : class, ISagaState
    where TEvent : class
    where TTimeout : class
{
    public Task ExecuteAsync(BehaviorContext<TSaga, TEvent> context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.AddCancelledTimeout(new CancelledTimeout(typeof(TTimeout)));
        return Task.CompletedTask;
    }
}
