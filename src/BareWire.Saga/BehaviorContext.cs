using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;

namespace BareWire.Saga;

internal sealed class BehaviorContext<TSaga, TEvent>
    where TSaga : class, ISagaState
    where TEvent : class
{
    private const int MaxPendingActions = 16;
    private readonly List<Func<ConsumeContext, Task>> _pendingActions = [];
    private readonly List<ScheduledTimeout> _scheduledTimeouts = [];
    private readonly List<CancelledTimeout> _cancelledTimeouts = [];

    internal BehaviorContext(TSaga saga, TEvent @event, ConsumeContext consumeContext)
    {
        ArgumentNullException.ThrowIfNull(saga);
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(consumeContext);
        Saga = saga;
        Event = @event;
        ConsumeContext = consumeContext;
    }

    internal TSaga Saga { get; }
    internal TEvent Event { get; }
    internal ConsumeContext ConsumeContext { get; }
    internal bool ShouldFinalize { get; set; }
    internal string? TargetState { get; set; }
    internal IReadOnlyList<Func<ConsumeContext, Task>> PendingActions => _pendingActions;
    internal IReadOnlyList<ScheduledTimeout> ScheduledTimeouts => _scheduledTimeouts;
    internal IReadOnlyList<CancelledTimeout> CancelledTimeouts => _cancelledTimeouts;

    internal void AddPendingAction(Func<ConsumeContext, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_pendingActions.Count >= MaxPendingActions)
        {
            throw new BareWireSagaException(
                $"Maximum pending actions per event ({MaxPendingActions}) exceeded for saga '{typeof(TSaga).Name}'.",
                typeof(TSaga),
                Saga.CorrelationId,
                Saga.CurrentState);
        }
        _pendingActions.Add(action);
    }

    internal void AddScheduledTimeout(ScheduledTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(timeout);
        _scheduledTimeouts.Add(timeout);
    }

    internal void AddCancelledTimeout(CancelledTimeout cancelled)
    {
        ArgumentNullException.ThrowIfNull(cancelled);
        _cancelledTimeouts.Add(cancelled);
    }
}

internal sealed record ScheduledTimeout(object Message, TimeSpan Delay, SchedulingStrategy Strategy, Type MessageType);

internal sealed record CancelledTimeout(Type MessageType);
