using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.RabbitMQ;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering the
/// BareWire RabbitMQ transport with the .NET dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the RabbitMQ transport adapter as <see cref="ITransportAdapter"/>
    /// with the dependency injection container. Call this before <c>AddBareWire()</c>
    /// or alongside it.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="configure">
    /// A delegate that configures the RabbitMQ transport via <see cref="IRabbitMqConfigurator"/>.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    public static IServiceCollection AddBareWireRabbitMq(
        this IServiceCollection services,
        Action<IRabbitMqConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new RabbitMqConfigurator();
        configure(configurator);
        var options = configurator.Build();

        services.TryAddSingleton(options);

        // Register routing key resolver with explicit mappings from MapRoutingKey<T>.
        // Uses Replace to always override the default resolver from AddBareWire().
        var routingKeyResolver = new Internal.RoutingKeyResolver(options.RoutingKeyMappings);
        services.Replace(ServiceDescriptor.Singleton<IRoutingKeyResolver>(routingKeyResolver));

        // Register exchange resolver with explicit mappings from MapExchange<T>.
        // Uses Replace to always override the default resolver from AddBareWire().
        var exchangeResolver = new Internal.ExchangeResolver(options.ExchangeMappings);
        services.Replace(ServiceDescriptor.Singleton<IExchangeResolver>(exchangeResolver));

        // Register the header mapper so both the transport adapter and request client share it.
        // When ConfigureHeaderMapping was called, the configurator flows through options.
        var headerMapper = new RabbitMqHeaderMapper(options.HeaderMappingConfigurator);
        services.TryAddSingleton(headerMapper);

        services.TryAddSingleton<ITransportAdapter>(sp => new RabbitMqTransportAdapter(
            sp.GetRequiredService<RabbitMqTransportOptions>(),
            sp.GetRequiredService<ILogger<RabbitMqTransportAdapter>>(),
            sp.GetRequiredService<RabbitMqHeaderMapper>()));
        services.TryAddSingleton<IRequestClientFactory>(sp => new RabbitMqRequestClientFactory(
            sp.GetRequiredService<RabbitMqTransportOptions>(),
            sp.GetRequiredService<ISerializerResolver>(),
            // Honour content-type-routed deserializers (e.g. MassTransit envelope) when registered;
            // fall back to wrapping the default deserializer for serializer packages that register none.
            sp.GetService<IDeserializerResolver>()
                ?? new Internal.SingleDeserializerResolver(sp.GetRequiredService<IMessageDeserializer>()),
            sp.GetRequiredService<IExchangeResolver>(),
            sp.GetRequiredService<IRoutingKeyResolver>(),
            sp.GetRequiredService<RabbitMqHeaderMapper>(),
            sp.GetRequiredService<ILoggerFactory>()));

        // Register topology so the core bus can deploy it on startup.
        if (options.Topology is not null)
        {
            services.TryAddSingleton(options.Topology);
        }

        // Register endpoint bindings so the core bus can start consume loops.
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
                DeadLetterExchange = options.Topology?.Queues
                    .FirstOrDefault(q => q.Name == e.QueueName)
                    ?.Arguments?.TryGetValue("x-dead-letter-exchange", out object? dlxObj) == true
                    ? dlxObj as string
                    : null,
                DeadLetterRoutingKey = options.Topology?.Queues
                    .FirstOrDefault(q => q.Name == e.QueueName)
                    ?.Arguments?.TryGetValue("x-dead-letter-routing-key", out object? dlxRkObj) == true
                    ? dlxRkObj as string
                    : null,
                SerializerOverrideType = e.SerializerOverrideType,
                DeserializerOverrideType = e.DeserializerOverrideType,
            })
            .ToList();
        services.TryAddSingleton<IReadOnlyList<EndpointBinding>>(bindings);

        return services;
    }
}
