using BareWire.Abstractions.Saga;

namespace BareWire.Saga.Activities;

internal sealed class PublishActivity<TSaga, TEvent, TMessage> : IActivityStep<TSaga, TEvent>
    where TSaga : class, ISagaState
    where TEvent : class
    where TMessage : class
{
    private readonly Func<TSaga, TEvent, TMessage> _factory;

    internal PublishActivity(Func<TSaga, TEvent, TMessage> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public Task ExecuteAsync(BehaviorContext<TSaga, TEvent> context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = _factory(context.Saga, context.Event);
        context.AddPendingAction(ctx => ctx.PublishAsync(message, cancellationToken));
        return Task.CompletedTask;
    }
}
