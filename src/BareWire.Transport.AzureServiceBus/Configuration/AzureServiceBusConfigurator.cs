namespace BareWire.Transport.AzureServiceBus.Configuration;

internal sealed class AzureServiceBusConfigurator : IAzureServiceBusConfigurator
{
    private string? _connectionString;
    private int? _prefetchCount;
    private int? _maxConcurrentCalls;

    // Auth mode fields (R2.4 — GAP-3: every field must be explicitly threaded through Build()).
    private AzureServiceBusAuthMode _authMode = AzureServiceBusAuthMode.Sas;
    private string? _fullyQualifiedNamespace;
    private Azure.Core.TokenCredential? _credential;

    // Session fields (R2.2 — GAP-3: every field must be explicitly threaded through Build()).
    private bool _enableSessions;
    private int _maxConcurrentSessions = 1;
    private TimeSpan? _sessionIdleTimeout;
    private TimeSpan? _maxAutoLockRenewDuration;

    public void UseSasAuth(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;
        _authMode = AzureServiceBusAuthMode.Sas;
    }

    public void ConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;
        _authMode = AzureServiceBusAuthMode.Sas;
    }

    public void UseEntraIdAuth(string fullyQualifiedNamespace, Azure.Core.TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullyQualifiedNamespace);
        ArgumentNullException.ThrowIfNull(credential);
        _fullyQualifiedNamespace = fullyQualifiedNamespace;
        _credential = credential;
        _authMode = AzureServiceBusAuthMode.EntraId;
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

        // Auth mode fields — GAP-3: every new field MUST be explicitly wired here.
        options.AuthMode = _authMode;

        if (_connectionString is not null)
        {
            options.ConnectionString = _connectionString;
        }

        if (_fullyQualifiedNamespace is not null)
        {
            options.FullyQualifiedNamespace = _fullyQualifiedNamespace;
        }

        if (_credential is not null)
        {
            options.Credential = _credential;
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
