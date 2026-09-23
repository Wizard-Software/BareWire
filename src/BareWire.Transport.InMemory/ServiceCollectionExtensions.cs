using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BareWire.Transport.InMemory;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering the BareWire
/// in-memory transport with the .NET dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory transport adapter as <see cref="ITransportAdapter"/> with the
    /// dependency injection container, along with a container-scoped <see cref="InMemoryBroker"/>
    /// singleton. Call this before <c>AddBareWire()</c> or alongside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// See the <see cref="IInMemoryConfigurator"/> remarks for the transport's delivery guarantee
    /// (explicitly at-most-once) and trust boundary (all publishers and consumers wired through this
    /// container share the trust boundary of the hosting process).
    /// </para>
    /// <para>
    /// Options are validated when this method builds them — a misconfiguration throws
    /// <see cref="BareWire.Abstractions.Exceptions.BareWireConfigurationException"/> here, during
    /// application startup, rather than at the first publish or consume call.
    /// </para>
    /// <para>
    /// Call this method once per <see cref="IServiceCollection"/>. A second call on the same collection
    /// still validates its options but is otherwise ignored as a whole — it does not create a second
    /// in-memory broker, replace the per-type routing mappings, or merge the two configurations.
    /// </para>
    /// </remarks>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="configure">
    /// A delegate that configures the in-memory transport via <see cref="IInMemoryConfigurator"/>.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="BareWire.Abstractions.Exceptions.BareWireConfigurationException">
    /// Thrown when the configured options fail validation (see <see cref="IInMemoryConfigurator"/> for
    /// the per-option rules).
    /// </exception>
    public static IServiceCollection AddBareWireInMemory(
        this IServiceCollection services,
        Action<IInMemoryConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new InMemoryConfigurator();
        configure(configurator);
        InMemoryTransportOptions options = configurator.Build();

        // A second call on the same collection is ignored as a whole (after its options were validated),
        // so its per-type resolvers cannot be mixed with the first call's topology and endpoint bindings.
        if (services.Any(d => d.ServiceType == typeof(InMemoryTransportOptions)))
        {
            return services;
        }

        services.TryAddSingleton(options);

        // Register routing key / exchange resolvers with the explicit mappings from MapRoutingKey<T> /
        // MapExchange<T>. Uses Replace to always override the default resolver from AddBareWire(),
        // mirroring the broker-backed transports — otherwise per-type mappings are captured but never
        // reach the PublishAsync<T> path.
        var routingKeyResolver = new RoutingKeyResolver(options.RoutingKeyMappings);
        services.Replace(ServiceDescriptor.Singleton<IRoutingKeyResolver>(routingKeyResolver));

        var exchangeResolver = new ExchangeResolver(options.ExchangeMappings);
        services.Replace(ServiceDescriptor.Singleton<IExchangeResolver>(exchangeResolver));

        services.TryAddSingleton(sp => new InMemoryBroker(sp.GetRequiredService<InMemoryTransportOptions>()));
        services.TryAddSingleton<ITransportAdapter>(sp => new InMemoryTransportAdapter(
            sp.GetRequiredService<InMemoryTransportOptions>(),
            sp.GetRequiredService<InMemoryBroker>()));

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
