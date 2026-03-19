using BareWire.Abstractions;
using BareWire.Abstractions.Saga;

namespace BareWire.Saga.Activities;

internal sealed class ScheduleTimeoutActivity<TSaga, TEvent, TTimeout> : IActivityStep<TSaga, TEvent>
    where TSaga : class, ISagaState
    where TEvent : class
    where TTimeout : class
{
    private readonly Func<TSaga, TEvent, TTimeout> _factory;
    private readonly ScheduleHandle<TTimeout> _schedule;

    internal ScheduleTimeoutActivity(Func<TSaga, TEvent, TTimeout> factory, ScheduleHandle<TTimeout> schedule)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(schedule);
        _factory = factory;
        _schedule = schedule;
    }

    public Task ExecuteAsync(BehaviorContext<TSaga, TEvent> context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = _factory(context.Saga, context.Event);
        context.AddScheduledTimeout(new ScheduledTimeout(message, _schedule.Delay, _schedule.Strategy, typeof(TTimeout)));
        return Task.CompletedTask;
    }
}
