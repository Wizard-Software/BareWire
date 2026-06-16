namespace BareWire.Transport.Kafka.Internal;

/// <summary>
/// Pure, deterministic exponential-backoff calculator for the retry-topic pattern (R1.3).
/// Maps a 1-based retry attempt number to a delay, capped at a maximum. No jitter (deterministic
/// for unit tests; jitter is a deployment concern and is intentionally omitted in R1.3).
/// </summary>
internal static class ExponentialBackoffCalculator
{
    /// <summary>
    /// Computes the backoff delay for the given retry attempt:
    /// <c>baseDelay * multiplier^(attempt-1)</c>, capped at <paramref name="maxDelay"/>.
    /// </summary>
    /// <param name="attempt">The 1-based retry attempt number (first retry = 1). Must be &gt;= 1.</param>
    /// <param name="baseDelay">The base delay for attempt 1.</param>
    /// <param name="multiplier">The exponential multiplier (&gt;= 1).</param>
    /// <param name="maxDelay">The maximum delay (cap).</param>
    /// <returns>The computed delay, never exceeding <paramref name="maxDelay"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="attempt"/> &lt; 1.</exception>
    /// <remarks>
    /// Overflow-safe: for large <paramref name="attempt"/> / <paramref name="multiplier"/>,
    /// <c>Math.Pow</c> may return <see cref="double.PositiveInfinity"/>; <see cref="Math.Min(double,double)"/>
    /// then correctly returns <paramref name="maxDelay"/> (PERF-2).
    /// </remarks>
    internal static TimeSpan ForAttempt(int attempt, TimeSpan baseDelay, double multiplier, TimeSpan maxDelay)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempt), attempt, "Retry attempt number must be 1-based (>= 1).");
        }

        double computedMs = baseDelay.TotalMilliseconds * Math.Pow(multiplier, attempt - 1);

        // Overflow-safe cap: Math.Min(Infinity, max) == max (PERF-2).
        double cappedMs = Math.Min(computedMs, maxDelay.TotalMilliseconds);

        return TimeSpan.FromMilliseconds(cappedMs);
    }
}
