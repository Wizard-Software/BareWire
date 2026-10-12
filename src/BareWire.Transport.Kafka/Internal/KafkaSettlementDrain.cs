using Microsoft.Extensions.Logging;

namespace BareWire.Transport.Kafka.Internal;

/// <summary>
/// The bounded wait a stopping consumer performs before it releases its settlement path (offset map,
/// native consumer): a caller that is still processing a message it already received (for example a per-key ordering
/// lane that finishes after the stop) gets a short window to settle it, so the settlement does not hit a
/// consumer that is already gone and the message does not come back after the next restart or rebalance.
/// </summary>
/// <remarks>
/// Shutdown path only; nothing here runs per message. Logs carry identifiers and counts only — never message
/// content, ordering keys, offsets or exception messages.
/// </remarks>
internal static partial class KafkaSettlementDrain
{
    /// <summary>How long a stopping consumer keeps its settlement path alive for a caller that is still working.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>The interval at which the pending count is re-checked. Shutdown path only.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Waits until <paramref name="pendingCount"/> reports no in-flight or in-progress settlement, the
    /// <paramref name="budget"/> is spent, or <paramref name="abortToken"/> is cancelled.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when every settlement finished; <see langword="false"/> when the budget ran out
    /// or the adapter is being disposed.
    /// </returns>
    internal static async Task<bool> WaitForSettlementsAsync(
        Func<int> pendingCount,
        TimeSpan budget,
        TimeProvider timeProvider,
        ILogger logger,
        string consumerId,
        string topic,
        CancellationToken abortToken)
    {
        ArgumentNullException.ThrowIfNull(pendingCount);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        // The adapter is being disposed: nothing is worth waiting for, its own shutdown releases everything.
        if (abortToken.IsCancellationRequested)
        {
            return false;
        }

        int pending = pendingCount();
        if (pending <= 0)
        {
            return true;
        }

        // A non-positive budget means "do not wait": release at once, as a consumer without this wait would.
        if (budget <= TimeSpan.Zero)
        {
            return false;
        }

        LogAwaitingSettlements(logger, consumerId, topic, pending);
        long start = timeProvider.GetTimestamp();
        try
        {
            while (true)
            {
                await Task.Delay(PollInterval, timeProvider, abortToken).ConfigureAwait(false);

                pending = pendingCount();
                if (pending <= 0)
                {
                    return true;
                }

                if (timeProvider.GetElapsedTime(start) >= budget)
                {
                    LogSettlementBudgetElapsed(logger, consumerId, topic, pending);
                    return false;
                }
            }
        }
        catch (OperationCanceledException) when (abortToken.IsCancellationRequested)
        {
            // The adapter started disposing while we waited: stop waiting, the shutdown sweep takes over.
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Consumer {ConsumerId} on topic '{Topic}' waits for {Count} pending settlement(s) before release.")]
    private static partial void LogAwaitingSettlements(ILogger logger, string consumerId, string topic, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Consumer {ConsumerId} on topic '{Topic}' released with {Count} pending settlement(s) after the settlement budget.")]
    private static partial void LogSettlementBudgetElapsed(ILogger logger, string consumerId, string topic, int count);
}
