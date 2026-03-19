using BareWire.Abstractions.Saga;

namespace BareWire.Saga.Activities;

internal interface IActivityStep<TSaga, TEvent>
    where TSaga : class, ISagaState
    where TEvent : class
{
    Task ExecuteAsync(BehaviorContext<TSaga, TEvent> context, CancellationToken cancellationToken = default);
}
