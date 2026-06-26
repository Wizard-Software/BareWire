using BareWire.Abstractions.Configuration;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.AzureServiceBus;

/// <summary>
/// Provides a single-call registration entry point that wires up both the BareWire core
/// engine and the Azure Service Bus transport in one statement.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers BareWire together with the Azure Service Bus transport in a single call. This
    /// is the recommended ergonomic entry point: it is equivalent to calling
    /// <see cref="BareWire.Transport.AzureServiceBus.ServiceCollectionExtensions.AddBareWireAzureServiceBus(IServiceCollection, Action{IAzureServiceBusConfigurator})"/>
    /// (which registers the <c>ITransportAdapter</c>) followed by
    /// <see cref="BareWire.ServiceCollectionExtensions.AddBareWire(IServiceCollection, Action{IBusConfigurator})"/>
    /// (which registers the core engine).
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="transport">Configures the Azure Service Bus transport via <see cref="IAzureServiceBusConfigurator"/> (connection string, topology, options).</param>
    /// <param name="bus">Optional core bus configuration via <see cref="IBusConfigurator"/> (endpoints, middleware, serializers). When omitted, the core is registered with defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="transport"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddBareWireWithAzureServiceBus(
        this IServiceCollection services,
        Action<IAzureServiceBusConfigurator> transport,
        Action<IBusConfigurator>? bus = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(transport);

        services.AddBareWireAzureServiceBus(transport);
        services.AddBareWire(bus ?? (_ => { }));
        return services;
    }
}
