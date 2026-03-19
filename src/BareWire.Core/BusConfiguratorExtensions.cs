using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Pipeline;
using BareWire.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BareWire.Core;

/// <summary>
/// Extension methods on <see cref="IBusConfigurator"/> and <see cref="IServiceCollection"/>
/// for registering built-in BareWire middleware with custom construction parameters.
/// </summary>
/// <remarks>
/// These extensions exist because certain middlewares (e.g. <c>PartitionerMiddleware</c>) require
/// constructor arguments that cannot be resolved through DI alone. Calling the extension method
/// pre-registers the middleware with a factory delegate so that the subsequent
/// <c>cfg.AddMiddleware&lt;T&gt;()</c> call uses the pre-registered factory rather than the
/// default type-only registration in <see cref="ServiceCollectionExtensions"/>.
/// </remarks>
public static class BusConfiguratorExtensions
{
    /// <summary>
    /// Registers <c>PartitionerMiddleware</c> in the DI container and adds it to the
    /// BareWire inbound message pipeline.
    /// </summary>
    /// <remarks>
    /// The middleware ensures that messages sharing the same partition key are never processed
    /// concurrently. Messages with different keys may be processed in parallel up to
    /// <paramref name="partitionCount"/> concurrent operations.
    ///
    /// The default key selector reads the <c>CorrelationId</c> header (parsed as a
    /// <see cref="Guid"/>) and falls back to <see cref="MessageContext.MessageId"/> when
    /// the header is absent or unparseable.
    ///
    /// Must be called <em>before</em> <c>services.AddBareWire(cfg =&gt; ...)</c> if you want to
    /// supply a custom <paramref name="keySelector"/>; or pass the returned configurator action
    /// into the <c>AddBareWire</c> delegate directly.
    ///
    /// <code>
    /// // Usage example
    /// services.AddPartitionerMiddleware(cfg, partitionCount: 64);
    /// services.AddBareWire(cfg =&gt; { ... });
    /// </code>
    /// </remarks>
    /// <param name="services">The service collection to register the middleware factory into.</param>
    /// <param name="busConfigurator">
    /// The <see cref="IBusConfigurator"/> instance received in the <c>AddBareWire</c> delegate.
    /// The middleware type is appended to the pipeline configuration.
    /// </param>
    /// <param name="partitionCount">
    /// Number of partitions (semaphores). Higher values allow more parallelism at the cost of
    /// slightly more memory. Must be at least 1. Typical values: 16, 32, 64.
    /// </param>
    /// <param name="keySelector">
    /// Optional function that derives a <see cref="Guid"/> partition key from the incoming
    /// <see cref="MessageContext"/>. When <see langword="null"/>, the default selector reads
    /// the <c>CorrelationId</c> header and falls back to <see cref="MessageContext.MessageId"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="partitionCount"/> is less than 1.
    /// </exception>
    public static IServiceCollection AddPartitionerMiddleware(
        this IServiceCollection services,
        IBusConfigurator busConfigurator,
        int partitionCount,
        Func<MessageContext, Guid>? keySelector = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(busConfigurator);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);

        // Pre-register with a factory so the DI container constructs PartitionerMiddleware
        // with the supplied arguments. ServiceCollectionExtensions.RegisterMiddleware uses
        // TryAddSingleton, which will skip this type once it is already registered here.
        services.TryAddSingleton<PartitionerMiddleware>(
            _ => new PartitionerMiddleware(partitionCount, keySelector));

        busConfigurator.AddMiddleware<PartitionerMiddleware>();

        return services;
    }
}
