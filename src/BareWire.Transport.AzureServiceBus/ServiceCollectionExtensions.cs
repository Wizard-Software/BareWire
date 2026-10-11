using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
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

        // Fail fast when another transport's adapter is already registered: the core bus resolves exactly one
        // non-keyed ITransportAdapter and one endpoint list, so this transport would be silently ignored.
        // Runs before configure(...) and any TryAdd/Replace so a rejected call leaves no partial registration.
        if (services.Any(d => d.ServiceType == typeof(ITransportAdapter) && !d.IsKeyedService)
            && !services.Any(d => d.ServiceType == typeof(AzureServiceBusTransportOptions)))
        {
            throw new BareWireConfigurationException(
                optionName: "AddBareWireAzureServiceBus",
                optionValue: "another transport is already registered",
                expectedValue: "a single transport per service collection. Multiple transports in one service " +
                               "collection are not supported yet: the bus resolves exactly one ITransportAdapter and " +
                               "one list of receive endpoints, so a second transport's adapter and endpoints would be " +
                               "ignored. Register only one AddBareWire<Transport>(...) call per service collection");
        }

        var configurator = new AzureServiceBusConfigurator();
        configure(configurator);
        AzureServiceBusTransportOptions options = configurator.Build();

        services.TryAddSingleton(options);

        // Register endpoint bindings so the core bus can start consume loops. Dead-letter exchange and
        // routing key stay null: the x-dead-letter-* arguments are AMQP-specific and this transport
        // dead-letters natively.
        List<EndpointBinding> bindings = options.EndpointConfigurations
            .Select(e => new EndpointBinding
            {
                EndpointName = e.QueueName,
                PrefetchCount = e.PrefetchCount,
                ConcurrentMessageLimit = e.ConcurrentMessageLimit,
                Ordering = e.Ordering,
                Consumers = e.ConsumerRegistrations,
                RawConsumers = e.RawConsumerTypes,
                SagaTypes = e.SagaTypes,
                RetryCount = e.RetryCount,
                RetryInterval = e.RetryInterval,
                SerializerOverrideType = e.SerializerOverrideType,
                DeserializerOverrideType = e.DeserializerOverrideType,
            })
            .ToList();
        services.TryAddSingleton<IReadOnlyList<EndpointBinding>>(bindings);

        services.TryAddSingleton<ITransportAdapter>(sp => new AzureServiceBusTransportAdapter(
            sp.GetRequiredService<AzureServiceBusTransportOptions>(),
            sp.GetRequiredService<ILogger<AzureServiceBusTransportAdapter>>()));

        return services;
    }
}
