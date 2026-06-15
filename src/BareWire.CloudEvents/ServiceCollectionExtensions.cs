using BareWire.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BareWire.CloudEvents;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering
/// BareWire CloudEvents binary-mode activation services with the .NET dependency injection container.
/// </summary>
/// <remarks>
/// This class is <see langword="public"/> and <see langword="static"/> because it contains
/// extension methods — an explicit exception to the <c>internal</c> visibility rule that
/// applies to all other implementation classes in <c>BareWire.CloudEvents</c>.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Activates CloudEvents 1.0 binary-mode support in the BareWire pipeline.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>AddBareWireJsonSerializer()</c> has not been called before this method.
    /// CloudEvents binary mode keeps the default raw JSON serializer (ADR-001) and only adds
    /// <c>ce-*</c> header binding on top of it; the base serializer must be registered first.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Binary-mode activation means:
    /// <list type="bullet">
    ///   <item><description>CE context attributes are mapped to/from <c>ce-*</c> transport headers
    ///   via <c>CloudEventBinaryHeaderMapper</c>.</description></item>
    ///   <item><description>The message payload is kept raw (no envelope) — ADR-001 and ADR-007.</description></item>
    ///   <item><description>A <see cref="CloudEventsBinaryActivation"/> singleton is registered as a
    ///   resolvable signal that binary mode was activated. Future pipeline components (e.g. structured-mode
    ///   router, 13.8/13.11) can depend on this marker to detect activation.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// This method does <em>not</em> replace the default <see cref="IMessageSerializer"/>
    /// (ADR-001 raw-first). It uses <c>TryAddSingleton</c> exclusively — calling this method
    /// twice is idempotent.
    /// </para>
    /// <code>
    /// services.AddBareWireJsonSerializer();  // registers default IMessageSerializer
    /// services.AddCloudEvents();             // activates binary-mode (ce-* header binding)
    ///
    /// // Publish with CE attributes:
    /// await bus.PublishCloudEventAsync(message, new CloudEventContext(id, source, type));
    ///
    /// // Consume with CE validation:
    /// ICloudEventAttributes attrs = context.GetCloudEventOrThrow();
    /// </code>
    /// </remarks>
    public static IServiceCollection AddCloudEvents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(d => d.ServiceType == typeof(IMessageSerializer)))
        {
            throw new InvalidOperationException(
                "Call AddBareWireJsonSerializer() before AddCloudEvents(). " +
                "CloudEvents binary mode keeps the default raw JSON serializer (ADR-001) and only " +
                "adds ce-* header binding on top of it.");
        }

        // TryAddSingleton — NEVER Replace (ADR-001). Registers the binary activation marker signal.
        // The validator (13.3) and mapper (13.4/13.5) are internal static; DI registers this
        // marker to signal that binary mode is active.
        services.TryAddSingleton<CloudEventsBinaryActivation>();

        return services;
    }
}
