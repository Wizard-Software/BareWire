using BareWire.Abstractions.Serialization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BareWire.Serialization.MsgPack;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering
/// BareWire MessagePack serialization services with the .NET dependency injection container.
/// </summary>
/// <remarks>
/// This class is <see langword="public"/> and <see langword="static"/> because it contains
/// extension methods — an explicit exception to the <c>internal</c> visibility rule that
/// applies to all other implementation classes in <c>BareWire.Serialization.MsgPack</c>.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the BareWire MessagePack serializer and deserializer with the
    /// dependency injection container.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Registers <see cref="MessagePackSerializer"/> as the <see cref="IMessageSerializer"/>
    /// and <see cref="MessagePackDeserializer"/> as the <see cref="IMessageDeserializer"/>.
    /// Both are registered as singletons — they are stateless and safe to share across scopes.
    /// </para>
    /// <para>
    /// Also registers <see cref="MessagePackDeserializer"/> under its <em>concrete type</em>
    /// (in addition to the <see cref="IMessageDeserializer"/> interface registration) so that
    /// per-endpoint overrides configured via <c>UseDeserializer&lt;MessagePackDeserializer&gt;()</c>
    /// resolve correctly at bus start via <c>GetRequiredService(typeof(MessagePackDeserializer))</c>.
    /// Without this registration <c>BareWireBusControl.StartAsync</c> would throw
    /// <see cref="InvalidOperationException"/> when the per-endpoint override is used (GAP-1).
    /// </para>
    /// <para>
    /// Uses <c>TryAdd*</c> variants so that a custom serializer registered earlier is not
    /// replaced. Call this method <em>after</em> any custom serializer registration.
    /// </para>
    /// <para>
    /// Content-Type routing (registering <c>IDeserializerResolver</c> so that incoming messages
    /// with <c>Content-Type: application/x-msgpack</c> are routed to this deserializer) is
    /// intentionally <b>not</b> performed here — call
    /// <see cref="AddBareWireMessagePackDeserializerRouting"/> separately after registering a
    /// base <c>IDeserializerResolver</c> (e.g. via <c>AddBareWireJsonSerializer()</c>).
    /// This keeps the default raw-JSON path intact (ADR-001).
    /// </para>
    /// </remarks>
    public static IServiceCollection AddBareWireMessagePackSerializer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMessageSerializer, MessagePackSerializer>();
        services.TryAddSingleton<IMessageDeserializer, MessagePackDeserializer>();

        // GAP-1: Register the concrete type so per-endpoint UseDeserializer<MessagePackDeserializer>()
        // resolves via GetRequiredService(typeof(MessagePackDeserializer)) in BareWireBusControl.
        services.TryAddSingleton<MessagePackDeserializer>();

        return services;
    }

    /// <summary>
    /// Activates MessagePack Content-Type routing in the BareWire consume pipeline.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>AddBareWireJsonSerializer()</c> (or another method that registers an
    /// <see cref="IDeserializerResolver"/>) has not been called before this method.
    /// MessagePack Content-Type routing decorates the existing <see cref="IDeserializerResolver"/>
    /// and keeps the default raw-JSON path intact (ADR-001, fail-closed behaviour).
    /// Call <c>AddBareWireJsonSerializer()</c> before <c>AddBareWireMessagePackDeserializerRouting()</c>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Routing activation means inbound messages with content type <c>application/x-msgpack</c>
    /// are routed to <see cref="MessagePackDeserializer"/>. All other content types (including
    /// <c>application/json</c> and <see langword="null"/>) continue to use the default raw-JSON
    /// deserializer — ADR-001 is preserved.
    /// </para>
    /// <para>
    /// Content-type comparison is an <b>exact-match</b> (<see cref="StringComparison.OrdinalIgnoreCase"/>).
    /// Parameterised variants such as <c>application/x-msgpack; charset=utf-8</c> are <b>not</b>
    /// matched and fall through to the inner resolver (fail-closed, ADR-001).
    /// </para>
    /// <para>
    /// Calling this method multiple times is idempotent — the decorator is never stacked.
    /// </para>
    /// <para>
    /// <b>Registration order:</b> <c>AddBareWireJsonSerializer()</c> (or equivalent) must be
    /// called first to register the base <c>IDeserializerResolver</c>. This method decorates
    /// the existing resolver; if none is registered an <see cref="InvalidOperationException"/>
    /// is thrown (fail-fast, same behaviour as <c>AddCloudEventsEnvelope()</c>).
    /// </para>
    /// <para>
    /// <b>Security note:</b> Enabling routing allows a peer that controls the
    /// <c>Content-Type</c> header to direct payloads into the MsgPack deserialization path.
    /// That path is hardened per ADR-013 (<c>UntrustedData</c> security profile: SipHash seed,
    /// recursion-depth limit, no Typeless/LZ4).
    /// </para>
    /// <para>
    /// <b>Message type visibility:</b> Message types deserialized via MessagePack must be
    /// <see langword="public"/>. <c>ContractlessStandardResolver</c> generates formatters only
    /// for <c>public</c> types.
    /// </para>
    /// <code>
    /// // Registration order matters:
    /// services.AddBareWireJsonSerializer();                  // registers IDeserializerResolver
    /// services.AddBareWireMessagePackSerializer();           // optional — registers serializer
    /// services.AddBareWireMessagePackDeserializerRouting();  // decorates IDeserializerResolver
    /// </code>
    /// </remarks>
    public static IServiceCollection AddBareWireMessagePackDeserializerRouting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotency guard (primary): marker-singleton is the only reliable guard because
        // a factory-based descriptor has ImplementationType == null — descriptor-type inspection
        // cannot detect our own decorator. Mirror of AddCloudEventsEnvelope (GAP-4 CloudEvents).
        if (services.Any(d => d.ServiceType == typeof(MessagePackDeserializerRoutingMarker)))
        {
            return services;
        }

        // Guard: IDeserializerResolver must already be registered (e.g. AddBareWireJsonSerializer called).
        ServiceDescriptor? existing = services.FirstOrDefault(d => d.ServiceType == typeof(IDeserializerResolver));
        if (existing is null)
        {
            throw new InvalidOperationException(
                "Call AddBareWireJsonSerializer() (or register an IDeserializerResolver) before " +
                "AddBareWireMessagePackDeserializerRouting(). MessagePack Content-Type routing " +
                "decorates the existing IDeserializerResolver and keeps the default raw-JSON path " +
                "intact (ADR-001).");
        }

        // GAP-1: Ensure the concrete type is registered so the routing factory can resolve it
        // directly via GetRequiredService<MessagePackDeserializer>() (GAP-2 fix).
        services.TryAddSingleton<MessagePackDeserializer>();
        services.TryAddSingleton<MessagePackDeserializerRoutingMarker>();

        // Replace the IDeserializerResolver descriptor with a decorating factory.
        // The captured 'existing' descriptor is rebuilt as the inner resolver within the factory.
        // NOTE: idempotency guard above ensures this block executes at most once per IServiceCollection.
        services.Remove(existing);
        services.AddSingleton<IDeserializerResolver>(sp =>
        {
            IDeserializerResolver inner = ResolveInner(existing, sp);
            // GAP-2: Use GetRequiredService<MessagePackDeserializer>() — NOT OfType<>().First() —
            // because TryAddSingleton<IMessageDeserializer, MessagePackDeserializer> is a no-op when
            // JSON was registered first, making MsgPack absent from IEnumerable<IMessageDeserializer>.
            return new MessagePackDeserializerRouter(inner, sp.GetRequiredService<MessagePackDeserializer>());
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
            "Cannot rebuild IDeserializerResolver from descriptor: " +
            "all of ImplementationFactory, ImplementationInstance, and ImplementationType are null.");
    }
}
