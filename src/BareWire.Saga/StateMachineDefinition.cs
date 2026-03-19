using System.Reflection;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;
using BareWire.Saga.Activities;

namespace BareWire.Saga;

internal sealed class StateMachineDefinition<TSaga>
    where TSaga : class, ISagaState
{
    private readonly Dictionary<string, Dictionary<Type, object>> _stateEventActivities;
    private readonly Dictionary<Type, object> _initialEventActivities;
    private readonly Dictionary<Type, object> _anyStateEventActivities;
    private readonly Dictionary<Type, object> _finalEventActivities;
    private readonly Dictionary<Type, Func<object, Guid>> _correlations;

    private StateMachineDefinition(
        Dictionary<string, Dictionary<Type, object>> stateEventActivities,
        Dictionary<Type, object> initialEventActivities,
        Dictionary<Type, object> anyStateEventActivities,
        Dictionary<Type, object> finalEventActivities,
        Dictionary<Type, Func<object, Guid>> correlations)
    {
        _stateEventActivities = stateEventActivities;
        _initialEventActivities = initialEventActivities;
        _anyStateEventActivities = anyStateEventActivities;
        _finalEventActivities = finalEventActivities;
        _correlations = correlations;
    }

    internal static StateMachineDefinition<TSaga> Build(BareWireStateMachine<TSaga> stateMachine)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);

        var stateActivities = new Dictionary<string, Dictionary<Type, object>>();
        foreach (var (state, registrations) in stateMachine.StateTransitions)
        {
            stateActivities[state] = ProcessRegistrations(registrations);
        }

        return new StateMachineDefinition<TSaga>(
            stateActivities,
            ProcessRegistrations(stateMachine.InitialTransitions),
            ProcessRegistrations(stateMachine.AnyStateTransitions),
            ProcessRegistrations(stateMachine.FinalTransitions),
            new Dictionary<Type, Func<object, Guid>>(stateMachine.Correlations));
    }

    private static Dictionary<Type, object> ProcessRegistrations(List<EventRegistration> registrations)
    {
        var result = new Dictionary<Type, object>();
        foreach (var reg in registrations)
        {
            var method = typeof(StateMachineDefinition<TSaga>)
                .GetMethod(nameof(BuildActivities), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(reg.EventType);
            var steps = method.Invoke(null, [reg.ConfigureDelegate])!;
            result[reg.EventType] = steps;
        }

        return result;
    }

    private static IReadOnlyList<IActivityStep<TSaga, TEvent>> BuildActivities<TEvent>(object configureDelegate)
        where TEvent : class
    {
        var configure = (Action<IEventActivityBuilder<TSaga, TEvent>>)configureDelegate;
        var builder = new EventActivityBuilder<TSaga, TEvent>();
        configure(builder);
        return builder.Steps;
    }

    internal IReadOnlyList<IActivityStep<TSaga, TEvent>>? GetActivities<TEvent>(string currentState)
        where TEvent : class
    {
        var eventType = typeof(TEvent);
        List<IActivityStep<TSaga, TEvent>>? combined = null;

        if (currentState == "Initial")
        {
            if (_initialEventActivities.TryGetValue(eventType, out var initialSteps))
            {
                combined = [.. (IReadOnlyList<IActivityStep<TSaga, TEvent>>)initialSteps];
            }
        }
        else if (_stateEventActivities.TryGetValue(currentState, out var stateEvents)
                 && stateEvents.TryGetValue(eventType, out var stateSteps))
        {
            combined = [.. (IReadOnlyList<IActivityStep<TSaga, TEvent>>)stateSteps];
        }

        if (_anyStateEventActivities.TryGetValue(eventType, out var anySteps))
        {
            var anyList = (IReadOnlyList<IActivityStep<TSaga, TEvent>>)anySteps;
            if (combined is null)
            {
                combined = [.. anyList];
            }
            else
            {
                combined.AddRange(anyList);
            }
        }

        return combined;
    }

    internal Guid GetCorrelationId<TEvent>(TEvent @event)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (_correlations.TryGetValue(typeof(TEvent), out var selector))
        {
            return selector(@event);
        }

        throw new BareWireSagaException(
            $"No correlation mapping registered for event type '{typeof(TEvent).Name}'.",
            typeof(TSaga),
            Guid.Empty);
    }
}
