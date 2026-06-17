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
    /// Uses <c>TryAdd*</c> variants so that a custom serializer registered earlier is not
    /// replaced. Call this method <em>after</em> any custom serializer registration.
    /// </para>
    /// <para>
    /// Content-Type routing (registering <c>IDeserializerResolver</c> so that incoming messages
    /// with <c>Content-Type: application/x-msgpack</c> are routed to this deserializer) is
    /// intentionally <b>not</b> performed here. That responsibility belongs to task R3.2
    /// (deserializer registry / Content-Type routing). Registering a router here would conflict
    /// with the single-router design of R3.2.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddBareWireMessagePackSerializer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMessageSerializer, MessagePackSerializer>();
        services.TryAddSingleton<IMessageDeserializer, MessagePackDeserializer>();

        return services;
    }
}
