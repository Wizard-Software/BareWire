using BareWire.Abstractions.Transport;
using BareWire.Transport.AWS.SQS.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.AWS.SQS;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering the
/// BareWire Amazon SQS transport with the .NET dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Amazon SQS transport adapter as <see cref="ITransportAdapter"/>
    /// with the dependency injection container. Call this before or alongside <c>AddBareWire()</c>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="configure">
    /// A delegate that configures the SQS transport via <see cref="ISqsConfigurator"/>.
    /// When not calling <see cref="ISqsConfigurator.UseExplicitCredentials"/>, the adapter
    /// defaults to the AWS SDK default credential chain (preferred for production).
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configure"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddBareWireSqs(
        this IServiceCollection services,
        Action<ISqsConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new SqsConfigurator();
        configure(configurator);
        SqsTransportOptions options = configurator.Build();

        services.TryAddSingleton(options);

        services.TryAddSingleton<ITransportAdapter>(sp => new SqsTransportAdapter(
            sp.GetRequiredService<SqsTransportOptions>(),
            sp.GetRequiredService<ILogger<SqsTransportAdapter>>()));

        return services;
    }
}
