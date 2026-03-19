using BareWire.Abstractions;
using BareWire.Abstractions.Saga;
using BareWire.Saga.Activities;

namespace BareWire.Saga;

internal sealed class EventActivityBuilder<TSaga, TEvent> : IEventActivityBuilder<TSaga, TEvent>
    where TSaga : class, ISagaState
    where TEvent : class
{
    private readonly List<IActivityStep<TSaga, TEvent>> _steps = [];

    internal IReadOnlyList<IActivityStep<TSaga, TEvent>> Steps => _steps;

    public IEventActivityBuilder<TSaga, TEvent> TransitionTo(string state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _steps.Add(new TransitionActivity<TSaga, TEvent>(state));
        return this;
    }

    public IEventActivityBuilder<TSaga, TEvent> Then(Func<TSaga, TEvent, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _steps.Add(new ThenActivity<TSaga, TEvent>(action));
        return this;
    }

    public IEventActivityBuilder<TSaga, TEvent> Publish<T>(Func<TSaga, TEvent, T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        _steps.Add(new PublishActivity<TSaga, TEvent, T>(factory));
        return this;
    }

    public IEventActivityBuilder<TSaga, TEvent> Send<T>(Uri destination, Func<TSaga, TEvent, T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(factory);
        _steps.Add(new SendActivity<TSaga, TEvent, T>(destination, factory));
        return this;
    }

    /// <summary>
    /// Schedules a timeout using a <see cref="ScheduleHandle{T}"/> for delay and strategy configuration.
    /// This internal overload is called by the state machine runtime when a handle is available.
    /// </summary>
    internal IEventActivityBuilder<TSaga, TEvent> ScheduleTimeout<T>(
        Func<TSaga, TEvent, T> factory,
        ScheduleHandle<T> schedule)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(schedule);
        _steps.Add(new ScheduleTimeoutActivity<TSaga, TEvent, T>(factory, schedule));
        return this;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// This overload satisfies the <see cref="IEventActivityBuilder{TSaga,TEvent}"/> interface contract.
    /// Because the delay and strategy come from the <see cref="ScheduleHandle{T}"/>, callers should
    /// prefer the internal <c>ScheduleTimeout(factory, handle)</c> overload. This overload uses a
    /// zero delay and <see cref="SchedulingStrategy.Auto"/> as defaults.
    /// </remarks>
    public IEventActivityBuilder<TSaga, TEvent> ScheduleTimeout<T>(Func<TSaga, TEvent, T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        var defaultHandle = new ScheduleHandle<T>(TimeSpan.Zero, SchedulingStrategy.Auto);
        _steps.Add(new ScheduleTimeoutActivity<TSaga, TEvent, T>(factory, defaultHandle));
        return this;
    }

    public IEventActivityBuilder<TSaga, TEvent> CancelTimeout<T>()
        where T : class
    {
        _steps.Add(new CancelTimeoutActivity<TSaga, TEvent, T>());
        return this;
    }

    public IEventActivityBuilder<TSaga, TEvent> Finalize()
    {
        _steps.Add(new FinalizeActivity<TSaga, TEvent>());
        return this;
    }
}
