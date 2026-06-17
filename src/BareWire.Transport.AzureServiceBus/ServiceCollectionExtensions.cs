using BareWire.Abstractions.Transport;
using BareWire.Transport.AzureServiceBus.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.AzureServiceBus;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering the
/// BareWire Azure Service Bus transport with the .NET dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Azure Service Bus transport adapter as <see cref="ITransportAdapter"/>
    /// with the dependency injection container. Call this before or alongside <c>AddBareWire()</c>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="configure">
    /// A delegate that configures the Azure Service Bus transport via
    /// <see cref="IAzureServiceBusConfigurator"/>. At minimum,
    /// <see cref="IAzureServiceBusConfigurator.ConnectionString"/> must be called.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configure"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddBareWireAzureServiceBus(
        this IServiceCollection services,
        Action<IAzureServiceBusConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new AzureServiceBusConfigurator();
        configure(configurator);
        AzureServiceBusTransportOptions options = configurator.Build();

        services.TryAddSingleton(options);

        services.TryAddSingleton<ITransportAdapter>(sp => new AzureServiceBusTransportAdapter(
            sp.GetRequiredService<AzureServiceBusTransportOptions>(),
            sp.GetRequiredService<ILogger<AzureServiceBusTransportAdapter>>()));

        return services;
    }
}
