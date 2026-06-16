using BareWire.Abstractions.Transport;
using BareWire.Transport.Kafka.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.Kafka;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering the
/// BareWire Kafka transport with the .NET dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Kafka transport adapter as <see cref="ITransportAdapter"/>
    /// with the dependency injection container. Call this before or alongside <c>AddBareWire()</c>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="configure">
    /// A delegate that configures the Kafka transport via <see cref="IKafkaConfigurator"/>.
    /// At minimum, <see cref="IKafkaConfigurator.BootstrapServers"/> must be called.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddBareWireKafka(
        this IServiceCollection services,
        Action<IKafkaConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new KafkaConfigurator();
        configure(configurator);
        KafkaTransportOptions options = configurator.Build();

        services.TryAddSingleton(options);

        services.TryAddSingleton<ITransportAdapter>(sp => new KafkaTransportAdapter(
            sp.GetRequiredService<KafkaTransportOptions>(),
            sp.GetRequiredService<ILogger<KafkaTransportAdapter>>()));

        return services;
    }
}
