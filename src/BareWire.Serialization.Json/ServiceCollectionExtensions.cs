using BareWire.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BareWire.Serialization.Json;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering
/// BareWire JSON serialization services with the .NET dependency injection container.
/// </summary>
/// <remarks>
/// This class is <see langword="public"/> and <see langword="static"/> because it contains
/// extension methods — an explicit exception to the <c>internal</c> visibility rule that
/// applies to all other implementation classes in <c>BareWire.Serialization.Json</c>.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the BareWire System.Text.Json serializer and deserializer with the
    /// dependency injection container.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Registers <see cref="SystemTextJsonSerializer"/> as the <see cref="IMessageSerializer"/>
    /// and <see cref="SystemTextJsonRawDeserializer"/> as the <see cref="IMessageDeserializer"/>.
    /// Both are registered as singletons — they are stateless and safe to share across scopes.
    /// </para>
    /// <para>
    /// Per ADR-001 (raw-first), <see cref="SystemTextJsonSerializer"/> produces plain JSON
    /// with no envelope wrapper. To opt into envelope format, register
    /// <see cref="BareWireEnvelopeSerializer"/> manually before calling <c>AddBareWire()</c>.
    /// </para>
    /// <para>
    /// Uses <c>TryAdd*</c> variants so that a custom serializer registered earlier is not
    /// replaced. Call this method <em>after</em> any custom serializer registration.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddBareWireJsonSerializer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMessageSerializer, SystemTextJsonSerializer>();
        services.TryAddSingleton<IMessageDeserializer, SystemTextJsonRawDeserializer>();

        return services;
    }
}
