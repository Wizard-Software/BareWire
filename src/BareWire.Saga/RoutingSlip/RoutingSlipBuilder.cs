using BareWire.Abstractions.Saga;

namespace BareWire.Saga.RoutingSlip;

/// <summary>
/// Fluent builder for constructing an ordered sequence of compensable activity slots
/// that form a routing slip.
/// </summary>
internal sealed class RoutingSlipBuilder
{
    private readonly List<IActivitySlot> _slots = [];

    /// <summary>
    /// Appends a compensable activity with its arguments to the routing slip.
    /// </summary>
    /// <typeparam name="TArgs">The activity's argument type.</typeparam>
    /// <typeparam name="TLog">The activity's compensation log type.</typeparam>
    /// <param name="activity">The activity to execute.</param>
    /// <param name="arguments">The arguments to pass to the activity.</param>
    /// <returns>This builder for chaining.</returns>
    internal RoutingSlipBuilder AddActivity<TArgs, TLog>(
        ICompensableActivity<TArgs, TLog> activity,
        TArgs arguments)
        where TArgs : class
        where TLog : class
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(arguments);
        _slots.Add(new ActivitySlot<TArgs, TLog>(activity, arguments));
        return this;
    }

    /// <summary>
    /// Returns the ordered, read-only list of activity slots in this routing slip.
    /// </summary>
    internal IReadOnlyList<IActivitySlot> Build() => _slots.AsReadOnly();
}
