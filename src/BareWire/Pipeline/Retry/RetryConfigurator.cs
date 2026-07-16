using BareWire.Abstractions.Configuration;

namespace BareWire.Pipeline.Retry;

internal sealed class RetryConfigurator : IRetryConfigurator
{
    private readonly TimeProvider? _timeProvider;
    private readonly List<Type> _handledExceptions = [];
    private readonly List<Type> _ignoredExceptions = [];

    // Holds the factory for the chosen strategy; set by Interval/Incremental/Exponential
    private Func<IReadOnlyList<Type>, IReadOnlyList<Type>, RetryPolicy>? _policyFactory;

    internal RetryConfigurator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider;
    }

    public IRetryConfigurator Interval(int retryCount, TimeSpan interval)
    {
        _policyFactory = (handled, ignored) =>
            new IntervalRetryPolicy(retryCount, interval, handled, ignored, _timeProvider);
        return this;
    }

    public IRetryConfigurator Incremental(int retryCount, TimeSpan initial, TimeSpan increment)
    {
        _policyFactory = (handled, ignored) =>
            new IncrementalRetryPolicy(retryCount, initial, increment, handled, ignored, _timeProvider);
        return this;
    }

    public IRetryConfigurator Exponential(int retryCount, TimeSpan minInterval, TimeSpan maxInterval)
    {
        _policyFactory = (handled, ignored) =>
            new ExponentialRetryPolicy(retryCount, minInterval, maxInterval, handled, ignored, _timeProvider);
        return this;
    }

    public IRetryConfigurator Handle<TException>() where TException : Exception
    {
        _handledExceptions.Add(typeof(TException));
        return this;
    }

    public IRetryConfigurator Ignore<TException>() where TException : Exception
    {
        _ignoredExceptions.Add(typeof(TException));
        return this;
    }

    public RetryPolicy Build()
    {
        if (_policyFactory is null)
            throw new InvalidOperationException(
                "Retry strategy must be configured. Call Interval(), Incremental(), or Exponential() first.");

        return _policyFactory(_handledExceptions.AsReadOnly(), _ignoredExceptions.AsReadOnly());
    }
}
