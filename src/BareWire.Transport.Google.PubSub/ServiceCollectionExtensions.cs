using BareWire.Abstractions.Transport;
using BareWire.Transport.Google.PubSub.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.Google.PubSub;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering the
/// BareWire Google Cloud Pub/Sub transport with the .NET dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Google Cloud Pub/Sub transport adapter as <see cref="ITransportAdapter"/>
    /// with the dependency injection container. Call this before or alongside <c>AddBareWire()</c>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="configure">
    /// A delegate that configures the Pub/Sub transport via <see cref="IPubSubConfigurator"/>.
    /// When not calling <see cref="IPubSubConfigurator.UseServiceAccountJson"/> or
    /// <see cref="IPubSubConfigurator.UseEmulator"/>, the adapter defaults to Google Application
    /// Default Credentials (preferred for production).
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configure"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddBareWirePubSub(
        this IServiceCollection services,
        Action<IPubSubConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new PubSubConfigurator();
        configure(configurator);
        PubSubTransportOptions options = configurator.Build();

        services.TryAddSingleton(options);

        services.TryAddSingleton<ITransportAdapter>(sp => new PubSubTransportAdapter(
            sp.GetRequiredService<PubSubTransportOptions>(),
            sp.GetRequiredService<ILogger<PubSubTransportAdapter>>()));

        return services;
    }
}
