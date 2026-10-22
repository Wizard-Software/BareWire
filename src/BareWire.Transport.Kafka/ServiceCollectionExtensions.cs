using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
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
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when this method was already called on <paramref name="services"/>, or when another
    /// transport is already registered in <paramref name="services"/>.
    /// </exception>
    public static IServiceCollection AddBareWireKafka(
        this IServiceCollection services,
        Action<IKafkaConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Fail fast on a repeated call for this transport: every registration below uses TryAdd, so a second
        // call's receive endpoints, topology and options would be silently ignored. Runs before configure(...)
        // and before any TryAdd/Replace, so a rejected call leaves the first registration untouched.
        if (services.Any(d => d.ServiceType == typeof(KafkaTransportOptions) && !d.IsKeyedService))
        {
            throw new BareWireConfigurationException(
                optionName: "AddBareWireKafka",
                optionValue: "already called for this service collection",
                expectedValue: "a single AddBareWireKafka(...) call per service collection. A second call's " +
                               "receive endpoints, topology and mappings would be ignored. Declare every receive " +
                               "endpoint, topology and mapping in one AddBareWireKafka(...) delegate");
        }

        // Fail fast when another transport's adapter is already registered: the core bus resolves exactly one
        // non-keyed ITransportAdapter and one endpoint list, so this transport would be silently ignored.
        // Runs before configure(...) and any TryAdd/Replace so a rejected call leaves no partial registration.
        if (services.Any(d => d.ServiceType == typeof(ITransportAdapter) && !d.IsKeyedService)
            && !services.Any(d => d.ServiceType == typeof(KafkaTransportOptions)))
        {
            throw new BareWireConfigurationException(
                optionName: "AddBareWireKafka",
                optionValue: "another transport is already registered",
                expectedValue: "a single transport per service collection. Multiple transports in one service " +
                               "collection are not supported yet: the bus resolves exactly one ITransportAdapter and " +
                               "one list of receive endpoints, so a second transport's adapter and endpoints would be " +
                               "ignored. Register only one AddBareWire<Transport>(...) call per service collection");
        }

        var configurator = new KafkaConfigurator();
        configure(configurator);
        KafkaTransportOptions options = configurator.Build();

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

        services.TryAddSingleton<ITransportAdapter>(sp => new KafkaTransportAdapter(
            sp.GetRequiredService<KafkaTransportOptions>(),
            sp.GetRequiredService<ILogger<KafkaTransportAdapter>>()));

        return services;
    }
}
