namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Public, fluent contract for configuring a per-consumer retry policy.
/// </summary>
/// <remarks>
/// The <b>chained</b> shape (every method returns <see cref="IRetryConfigurator"/>) is a
/// deliberate, accepted decision (I-3 ACCEPT-chain): retry-policy builders are conventionally
/// fluent, and publicizing the contract must not change its existing shape. The contract exposes
/// <b>only</b> configuration methods; building the policy (<c>Build()</c> returning the core
/// <c>RetryPolicy</c> type) stays in the <c>BareWire</c> core so that
/// <c>BareWire.Abstractions</c> remains dependency-free (zero-dep).
/// </remarks>
public interface IRetryConfigurator
{
    /// <summary>Retries a fixed number of times with a constant interval between attempts.</summary>
    /// <param name="retryCount">Maximum number of retry attempts.</param>
    /// <param name="interval">Constant delay between attempts.</param>
    /// <returns>The same configurator instance, enabling fluent chaining.</returns>
    IRetryConfigurator Interval(int retryCount, TimeSpan interval);

    /// <summary>Retries with a linearly increasing (incremental) interval.</summary>
    /// <param name="retryCount">Maximum number of retry attempts.</param>
    /// <param name="initial">Delay before the first retry.</param>
    /// <param name="increment">Amount added to the delay after each attempt.</param>
    /// <returns>The same configurator instance, enabling fluent chaining.</returns>
    IRetryConfigurator Incremental(int retryCount, TimeSpan initial, TimeSpan increment);

    /// <summary>Retries with an exponentially growing interval bounded to a range.</summary>
    /// <param name="retryCount">Maximum number of retry attempts.</param>
    /// <param name="minInterval">Lower bound for the delay.</param>
    /// <param name="maxInterval">Upper bound for the delay.</param>
    /// <returns>The same configurator instance, enabling fluent chaining.</returns>
    IRetryConfigurator Exponential(int retryCount, TimeSpan minInterval, TimeSpan maxInterval);

    /// <summary>Restricts retrying to the specified exception type (allow-list).</summary>
    /// <typeparam name="TException">Exception type that should trigger a retry.</typeparam>
    /// <returns>The same configurator instance, enabling fluent chaining.</returns>
    IRetryConfigurator Handle<TException>() where TException : Exception;

    /// <summary>Excludes the specified exception type from retrying (deny-list).</summary>
    /// <typeparam name="TException">Exception type that should never trigger a retry.</typeparam>
    /// <returns>The same configurator instance, enabling fluent chaining.</returns>
    IRetryConfigurator Ignore<TException>() where TException : Exception;
}
