using BareWire.Abstractions.Saga;

namespace BareWire.Saga.RoutingSlip;

/// <summary>
/// Type-erased abstraction over a single step in a routing slip, allowing heterogeneous
/// generic activity types to be stored in a uniform list.
/// </summary>
internal interface IActivitySlot
{
    /// <summary>
    /// Executes the activity and returns the log entry as a boxed object.
    /// </summary>
    Task<object> ExecuteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Compensates a previously executed activity using the boxed log entry.
    /// </summary>
    Task CompensateAsync(object log, CancellationToken cancellationToken);
}

/// <summary>
/// Closes over a concrete <see cref="ICompensableActivity{TArguments,TLog}"/> and its arguments,
/// providing type-erased execution and compensation via <see cref="IActivitySlot"/>.
/// </summary>
internal sealed class ActivitySlot<TArgs, TLog> : IActivitySlot
    where TArgs : class
    where TLog : class
{
    private readonly ICompensableActivity<TArgs, TLog> _activity;
    private readonly TArgs _arguments;

    internal ActivitySlot(ICompensableActivity<TArgs, TLog> activity, TArgs arguments)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(arguments);
        _activity = activity;
        _arguments = arguments;
    }

    public async Task<object> ExecuteAsync(CancellationToken cancellationToken)
    {
        TLog log = await _activity.ExecuteAsync(_arguments, cancellationToken).ConfigureAwait(false);
        return log;
    }

    public Task CompensateAsync(object log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        return _activity.CompensateAsync((TLog)log, cancellationToken);
    }
}
