namespace BareWire.Transport.AzureServiceBus;

/// <summary>
/// Specifies the authentication mode used to connect to an Azure Service Bus namespace.
/// </summary>
internal enum AzureServiceBusAuthMode
{
    /// <summary>
    /// Shared Access Signature via connection string (default, R2.1 behaviour).
    /// Use <c>UseSasAuth(connectionString)</c> or the legacy <c>ConnectionString(connectionString)</c>
    /// method on the configurator.
    /// </summary>
    Sas = 0,

    /// <summary>
    /// Azure Entra ID via a <see cref="Azure.Core.TokenCredential"/> against a fully-qualified
    /// namespace host (e.g. <c>myns.servicebus.windows.net</c>).
    /// Use <c>UseEntraIdAuth(fullyQualifiedNamespace, credential)</c> on the configurator.
    /// Token refresh is handled automatically by the Azure SDK — BareWire does not implement
    /// its own refresh loop.
    /// </summary>
    EntraId = 1,
}
