namespace BareWire.Pipeline.Retry;

/// <summary>
/// Upper bounds applied to every retry policy. They keep a misconfigured policy from pinning a delivery
/// (and its concurrency slot and flow-control credit) for an unbounded time, and keep every computed delay
/// inside the range accepted by <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>.
/// </summary>
internal static class RetryPolicyLimits
{
    /// <summary>The maximum number of retry attempts a policy may be configured with.</summary>
    internal const int MaxRetryCount = 100;

    /// <summary>The maximum delay of any single retry attempt.</summary>
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);

    /// <summary>Returns <paramref name="delay"/> clamped to <c>[TimeSpan.Zero, MaxDelay]</c>.</summary>
    internal static TimeSpan Clamp(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
            return TimeSpan.Zero;

        return delay > MaxDelay ? MaxDelay : delay;
    }
}
