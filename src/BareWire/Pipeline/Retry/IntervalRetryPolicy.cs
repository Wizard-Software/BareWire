namespace BareWire.Pipeline.Retry;

internal sealed class IntervalRetryPolicy : RetryPolicy
{
    private readonly TimeSpan _interval;

    internal IntervalRetryPolicy(
        int maxRetries,
        TimeSpan interval,
        IReadOnlyList<Type> handledExceptions,
        IReadOnlyList<Type> ignoredExceptions,
        TimeProvider? timeProvider = null)
        : base(maxRetries, handledExceptions, ignoredExceptions, timeProvider)
    {
        if (interval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be non-negative.");

        if (interval > RetryPolicyLimits.MaxDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                interval,
                $"Interval must not exceed {RetryPolicyLimits.MaxDelay}.");
        }

        _interval = interval;
    }

    internal override TimeSpan GetDelay(int attempt) => _interval;
}
