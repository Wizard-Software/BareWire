namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The bounded wait a stopping <see cref="InMemoryQueueRunner"/> performs before it gives up the deliveries
/// it handed out: a consumer that is still processing a message (for example a per-key ordering lane that
/// finishes after the stop) gets a short window to settle it, so the settlement is not turned into a no-op
/// followed by a requeued duplicate.
/// </summary>
internal static class InMemorySettlementDrain
{
    /// <summary>How long a stopping runner keeps its deliveries claimable for a consumer that is still working.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>The interval at which the unsettled count is re-checked. Shutdown path only.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Waits until <paramref name="pendingCount"/> reports no unsettled delivery, the <paramref name="budget"/>
    /// is spent, or <paramref name="abortToken"/> is cancelled.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when every delivery was settled; <see langword="false"/> when the budget ran
    /// out or the adapter is being disposed.
    /// </returns>
    internal static async Task<bool> WaitForSettlementsAsync(
        Func<int> pendingCount,
        TimeSpan budget,
        TimeProvider timeProvider,
        InMemoryConsumeDiagnostics diagnostics,
        string queueName,
        CancellationToken abortToken)
    {
        ArgumentNullException.ThrowIfNull(pendingCount);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(diagnostics);

        // The adapter is being disposed: nothing is worth waiting for, its own shutdown sweep drops everything.
        if (abortToken.IsCancellationRequested)
        {
            return false;
        }

        int pending = pendingCount();
        if (pending <= 0)
        {
            return true;
        }

        // A non-positive budget means "do not wait": release at once, as a runner without this wait would.
        if (budget <= TimeSpan.Zero)
        {
            return false;
        }

        diagnostics.SettlementDrainWaitStarted(queueName, pending);
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
                    diagnostics.SettlementBudgetElapsed(queueName, pending);
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
}
