using BareWire.Abstractions;
using BareWire.Abstractions.Saga;

namespace BareWire.Saga.Activities;

internal sealed class SendActivity<TSaga, TEvent, TMessage> : IActivityStep<TSaga, TEvent>
    where TSaga : class, ISagaState
    where TEvent : class
    where TMessage : class
{
    private readonly Uri _destination;
    private readonly Func<TSaga, TEvent, TMessage> _factory;

    internal SendActivity(Uri destination, Func<TSaga, TEvent, TMessage> factory)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(factory);
        _destination = destination;
        _factory = factory;
    }

    public Task ExecuteAsync(BehaviorContext<TSaga, TEvent> context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = _factory(context.Saga, context.Event);
        context.AddPendingAction(async ctx =>
        {
            ISendEndpoint endpoint = await ctx.GetSendEndpoint(_destination, cancellationToken).ConfigureAwait(false);
            await endpoint.SendAsync(message, cancellationToken).ConfigureAwait(false);
        });
        return Task.CompletedTask;
    }
}
