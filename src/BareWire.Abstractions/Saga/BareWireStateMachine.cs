namespace BareWire.Abstractions.Saga;

/// <summary>
/// A handle returned by <see cref="BareWireStateMachine{TSaga}.Event{T}"/> that identifies
/// a typed event within the state machine definition.
/// </summary>
/// <typeparam name="T">The event message type.</typeparam>
public sealed class EventHandle<T>
    where T : class
{
    /// <summary>Gets the name of the event derived from the message type.</summary>
    public string Name { get; } = typeof(T).Name;
}

/// <summary>
/// A handle returned by <see cref="BareWireStateMachine{TSaga}.State"/> that identifies
/// a named state within the state machine definition.
/// </summary>
public sealed class StateHandle
{
    internal StateHandle(string name) => Name = name;

    /// <summary>Gets the name of the state.</summary>
    public string Name { get; }
}

/// <summary>
/// A handle returned by <see cref="BareWireStateMachine{TSaga}.Schedule{T}"/> that identifies
/// a scheduled timeout within the state machine definition.
/// </summary>
/// <typeparam name="T">The timeout message type.</typeparam>
public sealed class ScheduleHandle<T>
    where T : class
{
    internal ScheduleHandle(TimeSpan delay, SchedulingStrategy strategy)
    {
        Delay = delay;
        Strategy = strategy;
    }

    /// <summary>Gets the configured delay for this scheduled timeout.</summary>
    public TimeSpan Delay { get; }

    /// <summary>Gets the scheduling strategy used to deliver the timeout message.</summary>
    public SchedulingStrategy Strategy { get; }
}

/// <summary>
/// Captures a single event-to-activity-chain mapping within a named state.
/// </summary>
/// <param name="EventType">The CLR type of the event message.</param>
/// <param name="ConfigureDelegate">
/// The configure delegate cast from <c>Action&lt;IEventActivityBuilder&lt;TSaga, TEvent&gt;&gt;</c>.
/// The BareWire.Saga runtime casts this back to the concrete delegate type before invoking it.
/// </param>
internal sealed record EventRegistration(Type EventType, object ConfigureDelegate);

/// <summary>
/// Base class for defining SAGA state machines using a fluent DSL.
/// </summary>
/// <typeparam name="TSaga">
/// The SAGA state type managed by this state machine. Must be a reference type implementing
/// <see cref="ISagaState"/>.
/// </typeparam>
/// <remarks>
/// <para>
/// Derive from this class and call the protected fluent methods (<see cref="Initially"/>,
/// <see cref="During(string, System.Action)"/>, <see cref="DuringAny"/>, <see cref="Finally"/>) inside the constructor
/// or an initialisation method to define the state machine topology.
/// </para>
/// <para>
/// The actual runtime execution engine is provided by the <c>BareWire.Saga</c> package.
/// This class exists in <c>BareWire.Abstractions</c> so that SAGA definitions can be authored
/// without a dependency on the infrastructure package.
/// </para>
/// </remarks>
public abstract class BareWireStateMachine<TSaga>
    where TSaga : class, ISagaState
{
    // Internal fields for recording state machine definition — accessed by BareWire.Saga runtime via IVT.
    internal readonly Dictionary<string, List<EventRegistration>> StateTransitions = [];
    internal readonly List<EventRegistration> InitialTransitions = [];
    internal readonly List<EventRegistration> AnyStateTransitions = [];
    internal readonly List<EventRegistration> FinalTransitions = [];
    internal readonly Dictionary<Type, Func<object, Guid>> Correlations = [];

    // Capture target for the current During/Initially/DuringAny/Finally scope.
    private List<EventRegistration>? _currentRegistrations;

    /// <summary>
    /// Defines a typed event that this state machine can handle.
    /// </summary>
    /// <typeparam name="T">The event message type. Must be a reference type.</typeparam>
    /// <returns>An <see cref="EventHandle{T}"/> that can be passed to <see cref="During(string, System.Action)"/> and other activity builders.</returns>
    protected EventHandle<T> Event<T>()
        where T : class
        => new();

    /// <summary>
    /// Defines a named state that the SAGA can be in.
    /// </summary>
    /// <param name="name">The unique name of the state within this state machine.</param>
    /// <returns>A <see cref="StateHandle"/> that can be passed to <see cref="During(string, System.Action)"/>.</returns>
    protected StateHandle State(string name)
        => new(name);

    /// <summary>
    /// Defines a scheduled timeout that can be started and cancelled during SAGA execution.
    /// </summary>
    /// <typeparam name="T">The timeout message type. Must be a reference type.</typeparam>
    /// <param name="configure">A delegate that configures the <see cref="IScheduleConfigurator"/> for this timeout.</param>
    /// <returns>
    /// A <see cref="ScheduleHandle{T}"/> that captures the configured delay and strategy.
    /// </returns>
    protected ScheduleHandle<T> Schedule<T>(Action<IScheduleConfigurator> configure)
        where T : class
    {
        var configurator = new DefaultScheduleConfigurator();
        configure(configurator);
        return new ScheduleHandle<T>(configurator.Delay, configurator.Strategy);
    }

    /// <summary>
    /// Registers an event handler within the current state scope (<see cref="During(string, System.Action)"/>,
    /// <see cref="Initially"/>, <see cref="DuringAny"/>, or <see cref="Finally"/>).
    /// The <paramref name="configure"/> delegate is stored and replayed by the BareWire.Saga
    /// runtime against its own <see cref="IEventActivityBuilder{TSaga,TEvent}"/> implementation.
    /// </summary>
    /// <typeparam name="T">The event message type. Must be a reference type.</typeparam>
    /// <param name="eventHandle">The event handle returned by <see cref="Event{T}"/>.</param>
    /// <param name="configure">
    /// A delegate that configures the activity chain for this event (e.g. state transitions,
    /// side-effects, scheduling).
    /// </param>
    protected void When<T>(EventHandle<T> eventHandle, Action<IEventActivityBuilder<TSaga, T>> configure)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(eventHandle);
        ArgumentNullException.ThrowIfNull(configure);

        _currentRegistrations?.Add(new EventRegistration(typeof(T), configure));
    }

    /// <summary>
    /// Configures the activities to execute when the SAGA is in a specific named state.
    /// </summary>
    /// <param name="state">The name of the state during which the activities apply.</param>
    /// <param name="configure">A delegate that defines the event handling activities for this state.</param>
    protected void During(string state, Action configure)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(configure);

        var registrations = new List<EventRegistration>();
        _currentRegistrations = registrations;
        configure();
        _currentRegistrations = null;

        if (!StateTransitions.TryGetValue(state, out var existing))
        {
            StateTransitions[state] = registrations;
        }
        else
        {
            existing.AddRange(registrations);
        }
    }

    /// <summary>
    /// Configures the activities to execute when the SAGA is in a specific named state.
    /// </summary>
    /// <param name="state">The state handle returned by <see cref="State"/>.</param>
    /// <param name="configure">A delegate that defines the event handling activities for this state.</param>
    protected void During(StateHandle state, Action configure)
    {
        ArgumentNullException.ThrowIfNull(state);
        During(state.Name, configure);
    }

    /// <summary>
    /// Configures the activities to execute when the SAGA receives an event in the initial
    /// (not-yet-started) state.
    /// </summary>
    /// <param name="configure">A delegate that defines the event handling activities for the initial state.</param>
    protected void Initially(Action configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _currentRegistrations = InitialTransitions;
        configure();
        _currentRegistrations = null;
    }

    /// <summary>
    /// Configures activities that apply regardless of the SAGA's current state.
    /// </summary>
    /// <param name="configure">A delegate that defines the event handling activities for any state.</param>
    protected void DuringAny(Action configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _currentRegistrations = AnyStateTransitions;
        configure();
        _currentRegistrations = null;
    }

    /// <summary>
    /// Configures cleanup activities that execute when the SAGA is finalized (deleted from the store).
    /// </summary>
    /// <param name="configure">A delegate that defines the final cleanup activities.</param>
    protected void Finally(Action configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _currentRegistrations = FinalTransitions;
        configure();
        _currentRegistrations = null;
    }

    /// <summary>
    /// Sets the correlation expression that maps a property on an event message to the
    /// <see cref="ISagaState.CorrelationId"/> of an existing SAGA instance.
    /// </summary>
    /// <typeparam name="T">The event message type. Must be a reference type.</typeparam>
    /// <param name="correlationExpression">
    /// A delegate that extracts the <see cref="Guid"/> correlation identifier from the event message.
    /// </param>
    protected void CorrelateBy<T>(Func<T, Guid> correlationExpression)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(correlationExpression);
        Correlations[typeof(T)] = obj => correlationExpression((T)obj);
    }

    /// <summary>
    /// Internal default implementation of <see cref="IScheduleConfigurator"/> used by <see cref="Schedule{T}"/>.
    /// </summary>
    private sealed class DefaultScheduleConfigurator : IScheduleConfigurator
    {
        /// <inheritdoc />
        public TimeSpan Delay { get; set; }

        /// <inheritdoc />
        public SchedulingStrategy Strategy { get; set; } = SchedulingStrategy.Auto;
    }
}
