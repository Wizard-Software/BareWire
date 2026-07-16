using System.Reflection;
using BareWire.Abstractions.Configuration;
using BareWire.Configuration;

namespace BareWire.Bus;

/// <summary>
/// Applies DI-registered <c>ConsumerDefinition&lt;TConsumer&gt;</c> instances to the already-materialized
/// <see cref="ConsumerRegistration"/>s produced by ordinary endpoint configuration, at bus start-up.
/// Discovery is driven purely by container registration (<see cref="IServiceProvider.GetService(Type)"/>): a
/// consumer type with no registered definition is left unchanged, and no assembly is ever scanned.
/// </summary>
/// <remarks>
/// All reflection here (<see cref="Type.MakeGenericType"/>, <see cref="MethodInfo.MakeGenericMethod"/>,
/// <see cref="IServiceProvider.GetService(Type)"/>) runs exactly once per registration, at start-up, when the
/// <c>BareWireBusControl</c> singleton factory resolves — never on the per-message consume path (ADR-003).
/// </remarks>
internal static class ConsumerDefinitionDiscovery
{
    /// <summary>
    /// The open generic definition of <see cref="ApplyOne{TConsumer}"/>, cached once and closed via
    /// <see cref="MethodInfo.MakeGenericMethod"/> per registration at start-up — mirrors the pattern used by
    /// <c>ReceiveEndpointConfiguration.TypedConsumerMethod</c>.
    /// </summary>
    private static readonly MethodInfo ApplyOneMethod = typeof(ConsumerDefinitionDiscovery)
        .GetMethod(nameof(ApplyOne), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Applies any DI-registered <c>ConsumerDefinition&lt;TConsumer&gt;</c> to each entry in
    /// <paramref name="registrations"/>, returning a list with the merged settings. When no registration has
    /// a matching definition, the original <paramref name="registrations"/> instance is returned unchanged
    /// (reference identity preserved — no allocation when discovery has nothing to apply).
    /// </summary>
    /// <param name="registrations">The consumer registrations to apply discovered definitions to.</param>
    /// <param name="services">The service provider definitions are resolved from.</param>
    /// <returns>
    /// <paramref name="registrations"/> unchanged if no definition matched; otherwise a new list with the
    /// merged registrations, preserving the original order and any unmatched entries as-is.
    /// </returns>
    internal static IReadOnlyList<ConsumerRegistration> ApplyRegisteredDefinitions(
        IReadOnlyList<ConsumerRegistration> registrations,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(services);

        ConsumerRegistration[]? result = null;
        for (int i = 0; i < registrations.Count; i++)
        {
            ConsumerRegistration registration = registrations[i];
            Type definitionType = typeof(ConsumerDefinition<>).MakeGenericType(registration.ConsumerType);
            object? definition = services.GetService(definitionType);
            if (definition is null)
            {
                result?[i] = registration;
                continue;
            }

            var merged = (ConsumerRegistration)ApplyOneMethod
                .MakeGenericMethod(registration.ConsumerType)
                .Invoke(null, [definition, registration])!;

            // Materialize the result list lazily — only once a definition actually changes something.
            result ??= [.. registrations];
            result[i] = merged;
        }

        return result ?? registrations;
    }

    /// <summary>
    /// Applies <see cref="ApplyRegisteredDefinitions"/> to every <see cref="EndpointBinding.Consumers"/> list
    /// across <paramref name="endpoints"/>, returning a list of endpoints with the merged consumer
    /// registrations. Endpoints whose consumers are unaffected are returned unchanged, and when no endpoint
    /// is affected the original <paramref name="endpoints"/> instance is returned (reference identity
    /// preserved).
    /// </summary>
    /// <param name="endpoints">The endpoint bindings to apply discovered definitions to.</param>
    /// <param name="services">The service provider definitions are resolved from.</param>
    /// <returns>
    /// <paramref name="endpoints"/> unchanged if no endpoint's consumers were affected; otherwise a new list
    /// with the affected endpoints replaced by their <see cref="EndpointBinding.WithConsumers"/> copies.
    /// </returns>
    internal static IReadOnlyList<EndpointBinding> ApplyToEndpoints(
        IReadOnlyList<EndpointBinding> endpoints,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(services);

        EndpointBinding[]? result = null;
        for (int i = 0; i < endpoints.Count; i++)
        {
            EndpointBinding endpoint = endpoints[i];
            IReadOnlyList<ConsumerRegistration> merged = ApplyRegisteredDefinitions(endpoint.Consumers, services);
            if (ReferenceEquals(merged, endpoint.Consumers))
            {
                result?[i] = endpoint;
                continue;
            }

            result ??= [.. endpoints];
            result[i] = endpoint.WithConsumers(merged);
        }

        return result ?? endpoints;
    }

    /// <summary>
    /// Invokes <paramref name="definition"/>'s <c>Configure</c> method through the internal invoker, then
    /// merges the resulting per-consumer settings into <paramref name="existing"/>. Closed once per
    /// registration via <see cref="ApplyOneMethod"/>.
    /// </summary>
    /// <typeparam name="TConsumer">The consumer implementation type.</typeparam>
    /// <param name="definition">The resolved <c>ConsumerDefinition&lt;TConsumer&gt;</c> instance.</param>
    /// <param name="existing">The already-materialized registration to merge settings into.</param>
    /// <returns>A new <see cref="ConsumerRegistration"/> carrying the merged settings.</returns>
    private static ConsumerRegistration ApplyOne<TConsumer>(object definition, ConsumerRegistration existing)
        where TConsumer : class
    {
        var typedDefinition = (ConsumerDefinition<TConsumer>)definition;
        var configurator = new ConsumerDefinitionConfigurator<TConsumer>();
        typedDefinition.ApplyConfiguration(new NoOpReceiveEndpointConfigurator(), configurator);
        return configurator.Merge(existing);
    }
}
