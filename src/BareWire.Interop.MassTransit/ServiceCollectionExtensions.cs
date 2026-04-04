using BareWire.Abstractions.Serialization;
using BareWire.Serialization.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BareWire.Interop.MassTransit;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering
/// BareWire MassTransit envelope deserialization services with the .NET dependency
/// injection container.
/// </summary>
/// <remarks>
/// This class is <see langword="public"/> and <see langword="static"/> because it contains
/// extension methods — an explicit exception to the <c>internal</c> visibility rule that
/// applies to all other implementation classes in <c>BareWire.Interop.MassTransit</c>.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MassTransitEnvelopeSerializer"/> in the DI container so it can be
    /// resolved per-endpoint via <c>UseSerializer&lt;MassTransitEnvelopeSerializer&gt;()</c>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>AddBareWireJsonSerializer()</c> has not been called before this method.
    /// The MassTransit serializer requires <see cref="IMessageSerializer"/> to be registered
    /// as the base JSON serializer.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method does <em>not</em> replace the default <see cref="IMessageSerializer"/>
    /// (ADR-001 raw-first). It only registers the concrete <see cref="MassTransitEnvelopeSerializer"/>
    /// type so per-endpoint overrides can resolve it from the container:
    /// </para>
    /// <code>
    /// services.AddBareWireJsonSerializer();           // registers default IMessageSerializer
    /// services.AddMassTransitEnvelopeSerializer();    // registers MassTransitEnvelopeSerializer
    ///
    /// // Per-endpoint override:
    /// rmq.ReceiveEndpoint("mt-queue", e =>
    /// {
    ///     e.UseSerializer&lt;MassTransitEnvelopeSerializer&gt;();
    /// });
    /// </code>
    /// </remarks>
    public static IServiceCollection AddMassTransitEnvelopeSerializer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(d => d.ServiceType == typeof(IMessageSerializer)))
        {
            throw new InvalidOperationException(
                "Call AddBareWireJsonSerializer() before AddMassTransitEnvelopeSerializer(). " +
                "The MassTransit serializer requires the base JSON serializer to be registered first.");
        }

        services.TryAddSingleton<MassTransitEnvelopeSerializer>();

        return services;
    }

    /// <summary>
    /// Registers the MassTransit envelope deserializer and updates the content-type router
    /// (<see cref="IDeserializerResolver"/>) to route <c>application/vnd.masstransit+json</c>
    /// messages to <see cref="MassTransitEnvelopeDeserializer"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>AddBareWireJsonSerializer()</c> has not been called before this method.
    /// The MassTransit deserializer requires <see cref="IMessageDeserializer"/> to be registered
    /// as the base JSON deserializer and fallback handler.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method must be called <em>after</em> <c>AddBareWireJsonSerializer()</c>:
    /// </para>
    /// <code>
    /// services.AddBareWireJsonSerializer();          // registers IMessageDeserializer + IDeserializerResolver
    /// services.AddMassTransitEnvelopeDeserializer(); // replaces IDeserializerResolver with MT-aware router
    /// </code>
    /// <para>
    /// Registers <see cref="MassTransitEnvelopeDeserializer"/> as a singleton and replaces
    /// the existing <see cref="IDeserializerResolver"/> registration with a new
    /// <c>ContentTypeDeserializerRouter</c> that forwards <c>application/vnd.masstransit+json</c>
    /// messages to <see cref="MassTransitEnvelopeDeserializer"/> and all other content types to
    /// the default deserializer.
    /// </para>
    /// <para>
    /// Uses <see cref="ServiceCollectionDescriptorExtensions.Replace"/> so the router registered
    /// by <c>AddBareWireJsonSerializer()</c> is fully replaced. Advanced users who need a custom
    /// router should register <see cref="IDeserializerResolver"/> manually after calling this method.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddMassTransitEnvelopeDeserializer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(d => d.ServiceType == typeof(IMessageDeserializer)))
        {
            throw new InvalidOperationException(
                "Call AddBareWireJsonSerializer() before AddMassTransitEnvelopeDeserializer(). " +
                "The MassTransit deserializer requires the base JSON deserializer to be registered first.");
        }

        services.AddSingleton<MassTransitEnvelopeDeserializer>();

        services.Replace(ServiceDescriptor.Singleton<IDeserializerResolver>(sp =>
        {
            var defaultDeserializer = sp.GetRequiredService<IMessageDeserializer>();
            var mtDeserializer = sp.GetRequiredService<MassTransitEnvelopeDeserializer>();
            return new ContentTypeDeserializerRouter(defaultDeserializer, [mtDeserializer]);
        }));

        return services;
    }
}
