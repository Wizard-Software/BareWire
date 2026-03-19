using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Core.Bus;
using BareWire.Core.Configuration;
using BareWire.Core.FlowControl;
using BareWire.Core.Observability;
using BareWire.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareWire.Core;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering
/// BareWire messaging services with the .NET dependency injection container.
/// </summary>
/// <remarks>
/// This class is <see langword="public"/> and <see langword="static"/> because it contains
/// extension methods — an explicit exception to the <c>internal</c> visibility rule that
/// applies to all other implementation classes in <c>BareWire.Core</c>.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers BareWire messaging services with the dependency injection container.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="configure">
    /// A delegate that configures the bus via <see cref="IBusConfigurator"/>.
    /// Transport, serializer, middleware, and receive endpoint settings are applied here.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// Configuration validation (transport registered, endpoints have consumers, valid prefetch counts)
    /// is deferred to <see cref="IBusControl.StartAsync"/> — not to DI registration time.
    /// This aligns with the fail-fast principle: errors surface when the host starts,
    /// not when the container is built.
    /// </remarks>
    public static IServiceCollection AddBareWire(
        this IServiceCollection services,
        Action<IBusConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        BusConfigurator configurator = new();
        configure(configurator);

        RegisterMiddleware(services, configurator);
        RegisterCoreServices(services, configurator);

        return services;
    }

    // ── Private registration helpers ───────────────────────────────────────────

    private static void RegisterMiddleware(IServiceCollection services, BusConfigurator configurator)
    {
        // Register each middleware type as a Singleton so that MiddlewareChain's factory
        // can resolve them by concrete type via GetRequiredService(type).
        // TryAddSingleton is used so that callers who pre-register a factory overload
        // (e.g. AddPartitionerMiddleware) are not overwritten by a plain type-only registration.
        foreach (Type middlewareType in configurator.MiddlewareTypes)
            services.TryAddSingleton(middlewareType);
    }

    private static void RegisterCoreServices(IServiceCollection services, BusConfigurator configurator)
    {
        // The BusConfigurator instance is registered so BareWireBusControl can perform
        // fail-fast validation in StartAsync via ConfigurationValidator.Validate(configurator).
        services.AddSingleton(configurator);

        // Register NullInstrumentation as the default IBareWireInstrumentation fallback.
        // AddBareWireObservability() (from BareWire.Observability) will replace this with
        // BareWireInstrumentation when observability is enabled.
        services.TryAddSingleton<IBareWireInstrumentation, NullInstrumentation>();

        // Flow controller — tracks credit/health per endpoint.
        services.AddSingleton(sp => new FlowController(
            sp.GetRequiredService<ILogger<FlowController>>()));

        // Consumer dispatcher — creates scoped DI lifetimes per message dispatch.
        services.AddSingleton(sp => new ConsumerDispatcher(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<ConsumerDispatcher>>()));

        // Middleware chain — built from all registered middleware types resolved from DI.
        // Capture the middleware type list from the configurator for use in the factory lambda.
        IReadOnlyList<Type> middlewareTypes = configurator.MiddlewareTypes;
        services.AddSingleton<MiddlewareChain>(sp =>
        {
            List<IMessageMiddleware> middlewares = new(middlewareTypes.Count);
            foreach (Type type in middlewareTypes)
                middlewares.Add((IMessageMiddleware)sp.GetRequiredService(type));

            return new MiddlewareChain(middlewares);
        });

        // Message pipeline — orchestrates inbound and outbound message lifecycle.
        services.AddSingleton(sp => new MessagePipeline(
            sp.GetRequiredService<MiddlewareChain>(),
            sp.GetRequiredService<ConsumerDispatcher>(),
            sp.GetRequiredService<IMessageDeserializer>(),
            sp.GetRequiredService<ILogger<MessagePipeline>>(),
            sp.GetRequiredService<IBareWireInstrumentation>()));

        // Publish flow control options — use defaults unless the caller has already registered
        // a custom instance before calling AddBareWire.
        // Use conditional registration to respect any user-supplied options.
        if (!services.Any(d => d.ServiceType == typeof(PublishFlowControlOptions)))
            services.AddSingleton(new PublishFlowControlOptions());

        // BareWireBus — the core bus implementation. Uses a factory because the constructor is internal.
        services.AddSingleton(sp => new BareWireBus(
            sp.GetRequiredService<ITransportAdapter>(),
            sp.GetRequiredService<IMessageSerializer>(),
            sp.GetRequiredService<MessagePipeline>(),
            sp.GetRequiredService<FlowController>(),
            sp.GetRequiredService<PublishFlowControlOptions>(),
            sp.GetRequiredService<ILogger<BareWireBus>>(),
            sp.GetRequiredService<IBareWireInstrumentation>()));

        // BareWireBusControl — wraps BareWireBus and implements IBusControl / IBus.
        // Uses a factory because the constructor is internal.
        services.AddSingleton(sp => new BareWireBusControl(
            sp.GetRequiredService<BareWireBus>(),
            sp.GetRequiredService<ITransportAdapter>(),
            sp.GetRequiredService<FlowController>(),
            sp.GetRequiredService<BusConfigurator>(),
            sp.GetRequiredService<ILogger<BareWireBusControl>>()));

        // IBusControl and IBus both resolve to the same BareWireBusControl singleton.
        services.AddSingleton<IBusControl>(sp => sp.GetRequiredService<BareWireBusControl>());
        services.AddSingleton<IBus>(sp => sp.GetRequiredService<BareWireBusControl>());

        // IPublishEndpoint and ISendEndpointProvider delegate to IBus.
        services.AddSingleton<IPublishEndpoint>(sp => sp.GetRequiredService<IBus>());
        services.AddSingleton<ISendEndpointProvider>(sp => sp.GetRequiredService<IBus>());

        // Hosted service — starts/stops the bus as part of the .NET generic host lifecycle.
        services.AddSingleton<IHostedService, BareWireBusHostedService>();
    }
}
