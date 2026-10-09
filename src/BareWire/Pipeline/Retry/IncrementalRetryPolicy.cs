namespace BareWire.Pipeline.Retry;

internal sealed class IncrementalRetryPolicy : RetryPolicy
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _increment;

    internal IncrementalRetryPolicy(
        int maxRetries,
        TimeSpan initial,
        TimeSpan increment,
        IReadOnlyList<Type> handledExceptions,
        IReadOnlyList<Type> ignoredExceptions,
        TimeProvider? timeProvider = null)
        : base(maxRetries, handledExceptions, ignoredExceptions, timeProvider)
    {
        if (initial < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initial), "Initial delay must be non-negative.");

        if (initial > RetryPolicyLimits.MaxDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initial),
                initial,
                $"Initial delay must not exceed {RetryPolicyLimits.MaxDelay}.");
        }

        // The largest delay is produced by the last retry (attempt index maxRetries - 1). Computed in 128 bits
        // so a huge increment cannot wrap around. A negative increment only shrinks delays, so it is allowed.
        Int128 largestTicks = (Int128)initial.Ticks + (Int128)increment.Ticks * Math.Max(maxRetries - 1, 0);
        if (largestTicks > RetryPolicyLimits.MaxDelay.Ticks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(increment),
                increment,
                $"The largest computed delay (initial + increment * (maxRetries - 1)) must not exceed {RetryPolicyLimits.MaxDelay}.");
        }

        _initial = initial;
        _increment = increment;
    }

    internal override TimeSpan GetDelay(int attempt)
    {
        // attempt is 0-based: first retry uses attempt=0.
        // 128-bit arithmetic cannot overflow here; the result is saturated to [0, MaxDelay].
        Int128 ticks = (Int128)_initial.Ticks + (Int128)_increment.Ticks * attempt;

        if (ticks <= 0)
            return TimeSpan.Zero;

        return ticks >= RetryPolicyLimits.MaxDelay.Ticks
            ? RetryPolicyLimits.MaxDelay
            : TimeSpan.FromTicks((long)ticks);
    }
}
