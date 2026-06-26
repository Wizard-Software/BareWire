using BareWire.Abstractions.Configuration;
using BareWire.Transport.Kafka;
using BareWire.Transport.Kafka.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.Kafka;

/// <summary>
/// Provides a single-call registration entry point that wires up both the BareWire core
/// engine and the Kafka transport in one statement.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers BareWire together with the Kafka transport in a single call. This is the
    /// recommended ergonomic entry point: it is equivalent to calling
    /// <see cref="BareWire.Transport.Kafka.ServiceCollectionExtensions.AddBareWireKafka(IServiceCollection, Action{IKafkaConfigurator})"/>
    /// (which registers the <c>ITransportAdapter</c>) followed by
    /// <see cref="BareWire.ServiceCollectionExtensions.AddBareWire(IServiceCollection, Action{IBusConfigurator})"/>
    /// (which registers the core engine).
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="transport">Configures the Kafka transport via <see cref="IKafkaConfigurator"/> (bootstrap servers, topology, options).</param>
    /// <param name="bus">Optional core bus configuration via <see cref="IBusConfigurator"/> (endpoints, middleware, serializers). When omitted, the core is registered with defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="transport"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddBareWireWithKafka(
        this IServiceCollection services,
        Action<IKafkaConfigurator> transport,
        Action<IBusConfigurator>? bus = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(transport);

        services.AddBareWireKafka(transport);
        services.AddBareWire(bus ?? (_ => { }));
        return services;
    }
}
