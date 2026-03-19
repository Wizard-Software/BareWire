using Microsoft.Extensions.Logging;

namespace BareWire.Saga.RoutingSlip;

/// <summary>
/// Executes an ordered sequence of compensable activities (a routing slip), automatically
/// compensating already-completed steps in reverse order if any step fails.
/// </summary>
internal sealed partial class RoutingSlipExecutor
{
    private readonly ILogger<RoutingSlipExecutor> _logger;

    internal RoutingSlipExecutor(ILogger<RoutingSlipExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Executes all activity slots sequentially. On failure, compensates completed steps
    /// in reverse order (best-effort). Returns a <see cref="RoutingSlipCompleted"/> or
    /// <see cref="RoutingSlipFaulted"/> result — never throws.
    /// </summary>
    /// <param name="slots">The ordered list of activity slots to execute.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    internal async Task<RoutingSlipResult> ExecuteAsync(
        IReadOnlyList<IActivitySlot> slots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slots);

        if (slots.Count is 0)
        {
            return new RoutingSlipCompleted([]);
        }

        var logs = new List<object>(slots.Count);

        for (int i = 0; i < slots.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LogExecutingStep(_logger, i, slots.Count);

            try
            {
                object log = await slots[i].ExecuteAsync(cancellationToken).ConfigureAwait(false);
                logs.Add(log);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogStepFailed(_logger, i, ex);
                bool compensationSucceeded = await CompensateAsync(slots, logs, i, cancellationToken)
                    .ConfigureAwait(false);
                return new RoutingSlipFaulted(ex, i, compensationSucceeded);
            }
        }

        return new RoutingSlipCompleted(logs.AsReadOnly());
    }

    /// <summary>
    /// Compensates all successfully completed steps in reverse order.
    /// Each compensation failure is logged but does not prevent subsequent compensations.
    /// </summary>
    /// <returns><c>true</c> if all compensations succeeded; <c>false</c> if any failed.</returns>
    private async Task<bool> CompensateAsync(
        IReadOnlyList<IActivitySlot> slots,
        List<object> logs,
        int failedAtStep,
        CancellationToken cancellationToken)
    {
        // Compensate steps 0..(failedAtStep-1) in reverse order.
        // logs[j] is the log for slots[j].
        bool allSucceeded = true;

        for (int j = failedAtStep - 1; j >= 0; j--)
        {
            LogCompensatingStep(_logger, j);

            try
            {
                await slots[j].CompensateAsync(logs[j], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCompensationFailed(_logger, j, ex);
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "RoutingSlip: executing step {StepIndex}/{TotalSteps}")]
    private static partial void LogExecutingStep(ILogger logger, int stepIndex, int totalSteps);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "RoutingSlip: step {StepIndex} faulted — beginning compensation")]
    private static partial void LogStepFailed(ILogger logger, int stepIndex, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "RoutingSlip: compensating step {StepIndex}")]
    private static partial void LogCompensatingStep(ILogger logger, int stepIndex);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "RoutingSlip: compensation of step {StepIndex} failed — continuing best-effort")]
    private static partial void LogCompensationFailed(ILogger logger, int stepIndex, Exception exception);
}
