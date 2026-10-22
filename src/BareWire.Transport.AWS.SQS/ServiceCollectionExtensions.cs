using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AWS.SQS.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.AWS.SQS;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering the
/// BareWire Amazon SQS transport with the .NET dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Amazon SQS transport adapter as <see cref="ITransportAdapter"/>
    /// with the dependency injection container. Call this before or alongside <c>AddBareWire()</c>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="configure">
    /// A delegate that configures the SQS transport via <see cref="ISqsConfigurator"/>.
    /// When not calling <see cref="ISqsConfigurator.UseExplicitCredentials"/>, the adapter
    /// defaults to the AWS SDK default credential chain (preferred for production).
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configure"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when this method was already called on <paramref name="services"/>, or when another
    /// transport is already registered in <paramref name="services"/>.
    /// </exception>
    public static IServiceCollection AddBareWireSqs(
        this IServiceCollection services,
        Action<ISqsConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Fail fast on a repeated call for this transport: every registration below uses TryAdd, so a second
        // call's receive endpoints, topology and options would be silently ignored. Runs before configure(...)
        // and before any TryAdd/Replace, so a rejected call leaves the first registration untouched.
        if (services.Any(d => d.ServiceType == typeof(SqsTransportOptions) && !d.IsKeyedService))
        {
            throw new BareWireConfigurationException(
                optionName: "AddBareWireSqs",
                optionValue: "already called for this service collection",
                expectedValue: "a single AddBareWireSqs(...) call per service collection. A second call's " +
                               "receive endpoints, topology and mappings would be ignored. Declare every receive " +
                               "endpoint, topology and mapping in one AddBareWireSqs(...) delegate");
        }

        // Fail fast when another transport's adapter is already registered: the core bus resolves exactly one
        // non-keyed ITransportAdapter and one endpoint list, so this transport would be silently ignored.
        // Runs before configure(...) and any TryAdd/Replace so a rejected call leaves no partial registration.
        if (services.Any(d => d.ServiceType == typeof(ITransportAdapter) && !d.IsKeyedService)
            && !services.Any(d => d.ServiceType == typeof(SqsTransportOptions)))
        {
            throw new BareWireConfigurationException(
                optionName: "AddBareWireSqs",
                optionValue: "another transport is already registered",
                expectedValue: "a single transport per service collection. Multiple transports in one service " +
                               "collection are not supported yet: the bus resolves exactly one ITransportAdapter and " +
                               "one list of receive endpoints, so a second transport's adapter and endpoints would be " +
                               "ignored. Register only one AddBareWire<Transport>(...) call per service collection");
        }

        var configurator = new SqsConfigurator();
        configure(configurator);
        SqsTransportOptions options = configurator.Build();

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

        services.TryAddSingleton<ITransportAdapter>(sp => new SqsTransportAdapter(
            sp.GetRequiredService<SqsTransportOptions>(),
            sp.GetRequiredService<ILogger<SqsTransportAdapter>>()));

        return services;
    }
}
