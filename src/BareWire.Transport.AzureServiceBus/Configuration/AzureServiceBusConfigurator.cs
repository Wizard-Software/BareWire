namespace BareWire.Transport.AzureServiceBus.Configuration;

internal sealed class AzureServiceBusConfigurator : IAzureServiceBusConfigurator
{
    private string? _connectionString;
    private int? _prefetchCount;
    private int? _maxConcurrentCalls;

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

        options.Validate();

        return options;
    }
}
