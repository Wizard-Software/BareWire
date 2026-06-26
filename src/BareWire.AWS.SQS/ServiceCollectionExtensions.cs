using BareWire.Abstractions.Configuration;
using BareWire.Transport.AWS.SQS;
using BareWire.Transport.AWS.SQS.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.AWS.SQS;

/// <summary>
/// Provides a single-call registration entry point that wires up both the BareWire core
/// engine and the AWS SQS transport in one statement.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers BareWire together with the AWS SQS transport in a single call. This is the
    /// recommended ergonomic entry point: it is equivalent to calling
    /// <see cref="BareWire.Transport.AWS.SQS.ServiceCollectionExtensions.AddBareWireSqs(IServiceCollection, Action{ISqsConfigurator})"/>
    /// (which registers the <c>ITransportAdapter</c>) followed by
    /// <see cref="BareWire.ServiceCollectionExtensions.AddBareWire(IServiceCollection, Action{IBusConfigurator})"/>
    /// (which registers the core engine).
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="transport">Configures the AWS SQS transport via <see cref="ISqsConfigurator"/> (region, credentials, topology, options).</param>
    /// <param name="bus">Optional core bus configuration via <see cref="IBusConfigurator"/> (endpoints, middleware, serializers). When omitted, the core is registered with defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="transport"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddBareWireWithSqs(
        this IServiceCollection services,
        Action<ISqsConfigurator> transport,
        Action<IBusConfigurator>? bus = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(transport);

        services.AddBareWireSqs(transport);
        services.AddBareWire(bus ?? (_ => { }));
        return services;
    }
}
