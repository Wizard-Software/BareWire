namespace BareWire.Transport.AzureServiceBus.Configuration;

internal sealed class AzureServiceBusConfigurator : IAzureServiceBusConfigurator
{
    private string? _connectionString;
    private int? _prefetchCount;
    private int? _maxConcurrentCalls;

    // Session fields (R2.2 — GAP-3: every field must be explicitly threaded through Build()).
    private bool _enableSessions;
    private int _maxConcurrentSessions = 1;
    private TimeSpan? _sessionIdleTimeout;
    private TimeSpan? _maxAutoLockRenewDuration;

    public void ConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;
    }

    public void PrefetchCount(int prefetchCount)
    {
        _prefetchCount = prefetchCount;
    }

    public void MaxConcurrentCalls(int maxConcurrentCalls)
    {
        _maxConcurrentCalls = maxConcurrentCalls;
    }

    // ── Session support (R2.2) ────────────────────────────────────────────────

    public void UseSessions(int maxConcurrentSessions = 1)
    {
        _enableSessions = true;
        _maxConcurrentSessions = maxConcurrentSessions;
    }

    public void SessionIdleTimeout(TimeSpan idleTimeout)
    {
        _sessionIdleTimeout = idleTimeout;
    }

    public void MaxAutoLockRenewDuration(TimeSpan duration)
    {
        _maxAutoLockRenewDuration = duration;
    }

    internal AzureServiceBusTransportOptions Build()
    {
        var options = new AzureServiceBusTransportOptions();

        if (_connectionString is not null)
        {
            options.ConnectionString = _connectionString;
        }

        if (_prefetchCount.HasValue)
        {
            options.PrefetchCount = _prefetchCount.Value;
        }

        if (_maxConcurrentCalls.HasValue)
        {
            options.MaxConcurrentCalls = _maxConcurrentCalls.Value;
        }

        // Session fields — GAP-3: every new field MUST be explicitly wired here.
        options.EnableSessions = _enableSessions;
        options.MaxConcurrentSessions = _maxConcurrentSessions;

        if (_sessionIdleTimeout.HasValue)
        {
            options.SessionIdleTimeout = _sessionIdleTimeout.Value;
        }

        if (_maxAutoLockRenewDuration.HasValue)
        {
            options.MaxAutoLockRenewDuration = _maxAutoLockRenewDuration.Value;
        }

        options.Validate();

        return options;
    }
}
