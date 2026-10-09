namespace BareWire.Pipeline.Retry;

internal sealed class ExponentialRetryPolicy : RetryPolicy
{
    private const int MaxExponent = 62;

    private readonly TimeSpan _minInterval;
    private readonly TimeSpan _maxInterval;

    internal ExponentialRetryPolicy(
        int maxRetries,
        TimeSpan minInterval,
        TimeSpan maxInterval,
        IReadOnlyList<Type> handledExceptions,
        IReadOnlyList<Type> ignoredExceptions,
        TimeProvider? timeProvider = null)
        : base(maxRetries, handledExceptions, ignoredExceptions, timeProvider)
    {
        if (minInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minInterval), "Minimum interval must be non-negative.");

        if (maxInterval < minInterval)
            throw new ArgumentOutOfRangeException(nameof(maxInterval), "Maximum interval must be >= minimum interval.");

        if (maxInterval > RetryPolicyLimits.MaxDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxInterval),
                maxInterval,
                $"Maximum interval must not exceed {RetryPolicyLimits.MaxDelay}.");
        }

        _minInterval = minInterval;
        _maxInterval = maxInterval;
    }

    internal override TimeSpan GetDelay(int attempt)
    {
        // Exponential: min * 2^attempt, capped at max, with jitter ±10%
        // The exponent is bounded so Math.Pow stays finite (min * Infinity would be Infinity, or NaN when min is 0).
        // 2^62 ticks already exceeds the maximum delay for any minInterval of at least one tick.
        double baseMs = _minInterval.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, MaxExponent));
        double cappedMs = Math.Min(baseMs, _maxInterval.TotalMilliseconds);

        // Add ±10% jitter to spread out concurrent retries
        double jitterFactor = 1.0 + (Random.Shared.NextDouble() - 0.5) * 0.2;
        double finalMs = cappedMs * jitterFactor;

        // Clamp to [minInterval, maxInterval]
        finalMs = Math.Max(_minInterval.TotalMilliseconds, Math.Min(finalMs, _maxInterval.TotalMilliseconds));

        return TimeSpan.FromMilliseconds(finalMs);
    }
}
