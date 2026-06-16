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

    /// <summary>
    /// Activates CloudEvents 1.0 structured-mode (envelope) routing in the BareWire consume pipeline.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>AddBareWireJsonSerializer()</c> has not been called before this method.
    /// Structured CloudEvents routing decorates the existing <see cref="IDeserializerResolver"/>
    /// and keeps the default raw-JSON path intact (ADR-001).
    /// Call <c>AddBareWireJsonSerializer()</c> before <c>AddCloudEventsEnvelope()</c>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Structured-mode activation means:
    /// <list type="bullet">
    ///   <item><description>
    ///     Inbound messages with content type <c>application/cloudevents+json</c> are routed to
    ///     <see cref="CloudEventsEnvelopeDeserializer"/>, which validates the CloudEvents 1.0
    ///     envelope structure and extracts the <c>data</c> payload (13.9 / 13.10).
    ///   </description></item>
    ///   <item><description>
    ///     All other content types (including <c>application/json</c> and <see langword="null"/>)
    ///     continue to use the default raw-JSON deserializer — ADR-001 is preserved.
    ///   </description></item>
    ///   <item><description>
    ///     A <see cref="CloudEventsEnvelopeActivation"/> singleton is registered as a resolvable
    ///     signal that structured mode was activated.
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// This method does <em>not</em> replace the default <see cref="IMessageSerializer"/>
    /// (ADR-001 raw-first). Only the <see cref="IDeserializerResolver"/> is decorated.
    /// Calling this method multiple times is idempotent — the decorator is never stacked.
    /// </para>
    /// <para>
    /// To publish a CloudEvents structured-mode message, use
    /// <see cref="CloudEventStructuredPublishExtensions.PublishCloudEventStructuredAsync{T}"/>.
    /// </para>
    /// <code>
    /// services.AddBareWireJsonSerializer();  // registers IMessageSerializer + IDeserializerResolver
    /// services.AddCloudEvents();             // optional — binary-mode header binding
    /// services.AddCloudEventsEnvelope();     // activates structured-mode routing (consume)
    ///
    /// // Publish a structured-mode envelope:
    /// await bus.PublishCloudEventStructuredAsync(message, new CloudEventContext(id, source, type));
    /// </code>
    /// </remarks>
    public static IServiceCollection AddCloudEventsEnvelope(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotency guard (GAP-4): marker-singleton is the PRIMARY mechanism.
        // A factory-based descriptor has ImplementationType == null, so descriptor-type inspection
        // cannot detect our own decorator. The marker singleton is the only reliable guard.
        if (services.Any(d => d.ServiceType == typeof(CloudEventsEnvelopeActivation)))
        {
            return services;
        }

        // Guard: IDeserializerResolver must already be registered (i.e. AddBareWireJsonSerializer called).
        ServiceDescriptor? existing = services.FirstOrDefault(d => d.ServiceType == typeof(IDeserializerResolver));
        if (existing is null)
        {
            throw new InvalidOperationException(
                "Call AddBareWireJsonSerializer() before AddCloudEventsEnvelope(). " +
                "Structured CloudEvents routing decorates the existing IDeserializerResolver " +
                "and keeps the default raw-JSON path intact (ADR-001).");
        }

        // Use factory overload — CloudEventsEnvelopeDeserializer has an internal constructor,
        // so type-based TryAddSingleton<T>() would fail at resolution time (DI requires public ctor).
        services.TryAddSingleton<CloudEventsEnvelopeDeserializer>(_ => new CloudEventsEnvelopeDeserializer());
        services.TryAddSingleton<CloudEventsEnvelopeActivation>(_ => new CloudEventsEnvelopeActivation());

        // Replace the IDeserializerResolver descriptor with a decorating factory.
        // The captured 'existing' descriptor is rebuilt as the inner resolver within the new factory.
        // NOTE: idempotency guard above ensures this block is executed at most once per IServiceCollection.
        services.Remove(existing);
        services.AddSingleton<IDeserializerResolver>(sp =>
        {
            IDeserializerResolver inner = ResolveInner(existing, sp);
            return new ContentTypeDeserializerRouter(inner, sp.GetRequiredService<CloudEventsEnvelopeDeserializer>());
        });

        return services;
    }

    /// <summary>
    /// Rebuilds the original <see cref="IDeserializerResolver"/> instance from a captured
    /// <see cref="ServiceDescriptor"/> — handles all three descriptor shapes defensively.
    /// </summary>
    private static IDeserializerResolver ResolveInner(ServiceDescriptor descriptor, IServiceProvider sp)
    {
        if (descriptor.ImplementationFactory is not null)
        {
            return (IDeserializerResolver)descriptor.ImplementationFactory(sp);
        }

        if (descriptor.ImplementationInstance is not null)
        {
            return (IDeserializerResolver)descriptor.ImplementationInstance;
        }

        if (descriptor.ImplementationType is not null)
        {
            return (IDeserializerResolver)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
        }

        throw new InvalidOperationException(
            $"Cannot rebuild IDeserializerResolver from descriptor: " +
            $"all of ImplementationFactory, ImplementationInstance, and ImplementationType are null.");
    }
}
