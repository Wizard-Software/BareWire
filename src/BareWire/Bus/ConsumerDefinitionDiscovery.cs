using System.Reflection;
using System.Runtime.ExceptionServices;
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
    /// <paramref name="registrations"/>, returning a list with the merged per-consumer settings. When no
    /// registration has a matching definition, the original <paramref name="registrations"/> instance is
    /// returned unchanged (reference identity preserved — no allocation when discovery has nothing to apply).
    /// </summary>
    /// <remarks>
    /// This overload merges only the <em>per-consumer</em> settings (routing keys, AcceptUntyped, envelope,
    /// retry policy). Endpoint-level settings a definition applies through the <c>endpoint</c> argument are
    /// materialized by <see cref="ApplyToEndpoints"/>, which owns the <see cref="EndpointBinding"/>; this
    /// list-only helper discards them (there is no endpoint here). Unsupported endpoint operations still
    /// throw via <see cref="CapturingReceiveEndpointConfigurator"/> rather than being silently ignored.
    /// </remarks>
    /// <param name="registrations">The consumer registrations to apply discovered definitions to.</param>
    /// <param name="services">The service provider definitions are resolved from.</param>
    /// <returns>
    /// <paramref name="registrations"/> unchanged if no definition matched; otherwise a new list with the
    /// merged registrations, preserving the original order and any unmatched entries as-is.
    /// </returns>
    internal static IReadOnlyList<ConsumerRegistration> ApplyRegisteredDefinitions(
        IReadOnlyList<ConsumerRegistration> registrations,
        IServiceProvider services)
        => ApplyRegisteredDefinitions(registrations, services, new CapturingReceiveEndpointConfigurator());

    private static IReadOnlyList<ConsumerRegistration> ApplyRegisteredDefinitions(
        IReadOnlyList<ConsumerRegistration> registrations,
        IServiceProvider services,
        IReceiveEndpointConfigurator endpoint)
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

            ConsumerRegistration merged;
            try
            {
                merged = (ConsumerRegistration)ApplyOneMethod
                    .MakeGenericMethod(registration.ConsumerType)
                    .Invoke(null, [definition, registration, endpoint])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                // A definition's Configure can throw (e.g. NotSupportedException for an endpoint operation a
                // per-consumer definition cannot express); surface the real exception, not the reflection wrapper.
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw; // unreachable — satisfies definite-assignment
            }

            // Materialize the result list lazily — only once a definition actually changes something.
            result ??= [.. registrations];
            result[i] = merged;
        }

        return result ?? registrations;
    }

    /// <summary>
    /// Applies discovered definitions to every <see cref="EndpointBinding.Consumers"/> list across
    /// <paramref name="endpoints"/>, materializing both the merged per-consumer settings and the endpoint-level
    /// settings a definition applied through the <c>endpoint</c> argument of its <c>Configure</c> method
    /// (prefetch, concurrency, scalar retry, serializer/deserializer overrides). Endpoints whose consumers and
    /// endpoint settings are unaffected are returned unchanged, and when no endpoint is affected the original
    /// <paramref name="endpoints"/> instance is returned (reference identity preserved).
    /// </summary>
    /// <param name="endpoints">The endpoint bindings to apply discovered definitions to.</param>
    /// <param name="services">The service provider definitions are resolved from.</param>
    /// <returns>
    /// <paramref name="endpoints"/> unchanged if no endpoint was affected; otherwise a new list with the
    /// affected endpoints replaced by copies carrying the merged consumers and endpoint settings.
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

            // One capturer per endpoint, seeded from its current values and shared across every consumer's
            // Configure call — the last definition to set an endpoint-level scalar wins.
            var capturing = new CapturingReceiveEndpointConfigurator(endpoint);
            IReadOnlyList<ConsumerRegistration> merged =
                ApplyRegisteredDefinitions(endpoint.Consumers, services, capturing);

            bool consumersChanged = !ReferenceEquals(merged, endpoint.Consumers);
            if (!consumersChanged && !capturing.IsDirty)
            {
                result?[i] = endpoint;
                continue;
            }

            result ??= [.. endpoints];
            result[i] = endpoint.WithConsumersAndEndpointSettings(
                merged,
                capturing.PrefetchCount,
                capturing.ConcurrentMessageLimit,
                capturing.RetryCount,
                capturing.RetryInterval,
                capturing.CapturedSerializerOverrideType,
                capturing.CapturedDeserializerOverrideType);
        }

        return result ?? endpoints;
    }

    /// <summary>
    /// Invokes <paramref name="definition"/>'s <c>Configure</c> method through the internal invoker — passing
    /// <paramref name="endpoint"/> as the endpoint argument (so endpoint-level settings are captured) and a
    /// per-consumer façade — then merges the resulting per-consumer settings into <paramref name="existing"/>.
    /// Closed once per registration via <see cref="ApplyOneMethod"/>.
    /// </summary>
    /// <typeparam name="TConsumer">The consumer implementation type.</typeparam>
    /// <param name="definition">The resolved <c>ConsumerDefinition&lt;TConsumer&gt;</c> instance.</param>
    /// <param name="existing">The already-materialized registration to merge settings into.</param>
    /// <param name="endpoint">The (capturing) endpoint configurator handed to <c>Configure</c>.</param>
    /// <returns>A new <see cref="ConsumerRegistration"/> carrying the merged settings.</returns>
    private static ConsumerRegistration ApplyOne<TConsumer>(
        object definition,
        ConsumerRegistration existing,
        IReceiveEndpointConfigurator endpoint)
        where TConsumer : class
    {
        var typedDefinition = (ConsumerDefinition<TConsumer>)definition;
        var configurator = new ConsumerDefinitionConfigurator<TConsumer>();
        typedDefinition.ApplyConfiguration(endpoint, configurator);
        return configurator.Merge(existing);
    }
}
