namespace BareWire.Transport.AzureServiceBus.Configuration;

/// <summary>
/// Provides a fluent API for configuring the Azure Service Bus transport adapter.
/// Obtained via <see cref="ServiceCollectionExtensions.AddBareWireAzureServiceBus"/>.
/// </summary>
/// <remarks>
/// R2.1: connection-string auth, prefetch count, and max concurrent calls.
/// Full SAS token refresh and Azure Entra ID (<c>DefaultAzureCredential</c>) are deferred to R2.4.
/// Session support (R2.2) and native scheduled messages (R2.3) will extend this interface.
/// </remarks>
public interface IAzureServiceBusConfigurator
{
    /// <summary>
    /// Configures the Azure Service Bus connection string (SAS connection string).
    /// Must be called before the bus is started.
    /// </summary>
    /// <param name="connectionString">
    /// The Service Bus connection string containing the namespace endpoint and SAS credentials.
    /// Format: <c>Endpoint=sb://&lt;namespace&gt;.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...</c>
    /// Must not be <see langword="null"/> or empty. Never logged or stored in diagnostic output.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="connectionString"/> is <see langword="null"/> or empty.
    /// </exception>
    void ConnectionString(string connectionString);

    /// <summary>
    /// Configures the number of messages the receiver will pre-fetch from the broker.
    /// Defaults to <c>0</c> (no pre-fetch) when not called.
    /// </summary>
    /// <param name="prefetchCount">
    /// The pre-fetch count. A value of <c>0</c> disables pre-fetching (safest for PeekLock).
    /// </param>
    /// <remarks>
    /// See <c>AzureServiceBusTransportOptions.PrefetchCount</c> for lock-expiry risk guidance.
    /// </remarks>
    void PrefetchCount(int prefetchCount);

    /// <summary>
    /// Configures the maximum number of messages processed concurrently per consumer.
    /// Defaults to <c>1</c> when not called.
    /// </summary>
    /// <param name="maxConcurrentCalls">The maximum concurrent message count. Must be at least 1.</param>
    void MaxConcurrentCalls(int maxConcurrentCalls);
}
