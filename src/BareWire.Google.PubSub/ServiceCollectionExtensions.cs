using BareWire.Abstractions.Configuration;
using BareWire.Transport.Google.PubSub;
using BareWire.Transport.Google.PubSub.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.Google.PubSub;

/// <summary>
/// Provides a single-call registration entry point that wires up both the BareWire core
/// engine and the Google Cloud Pub/Sub transport in one statement.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers BareWire together with the Google Cloud Pub/Sub transport in a single call.
    /// This is the recommended ergonomic entry point: it is equivalent to calling
    /// <see cref="BareWire.Transport.Google.PubSub.ServiceCollectionExtensions.AddBareWirePubSub(IServiceCollection, Action{IPubSubConfigurator})"/>
    /// (which registers the <c>ITransportAdapter</c>) followed by
    /// <see cref="BareWire.ServiceCollectionExtensions.AddBareWire(IServiceCollection, Action{IBusConfigurator})"/>
    /// (which registers the core engine).
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="transport">Configures the Google Cloud Pub/Sub transport via <see cref="IPubSubConfigurator"/> (project id, topology, options).</param>
    /// <param name="bus">Optional core bus configuration via <see cref="IBusConfigurator"/> (endpoints, middleware, serializers). When omitted, the core is registered with defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="transport"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddBareWireWithPubSub(
        this IServiceCollection services,
        Action<IPubSubConfigurator> transport,
        Action<IBusConfigurator>? bus = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(transport);

        services.AddBareWirePubSub(transport);
        services.AddBareWire(bus ?? (_ => { }));
        return services;
    }
}
