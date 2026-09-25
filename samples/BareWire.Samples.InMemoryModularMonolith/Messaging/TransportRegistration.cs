using BareWire.Abstractions.Configuration;
using BareWire.InMemory;
using BareWire.RabbitMQ;

using Microsoft.Extensions.DependencyInjection;

namespace BareWire.Samples.InMemoryModularMonolith.Messaging;

/// <summary>
/// The single place where the two transports diverge. Both branches call
/// <see cref="ModulithMessaging.Configure"/> with the same topology, default exchange, and receive
/// endpoints — only the registration call and the RabbitMQ host connection differ.
/// </summary>
internal static class TransportRegistration
{
    /// <summary>
    /// Registers BareWire, wired for the modular monolith's topology and consumers, on the transport
    /// selected by <paramref name="transport"/>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="transport">The selected transport.</param>
    /// <param name="rabbitMqConnectionString">
    /// The RabbitMQ connection URI. Only read when <paramref name="transport"/> is
    /// <see cref="TransportKind.RabbitMQ"/>.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="transport"/> is not a known <see cref="TransportKind"/> member.
    /// </exception>
    public static IServiceCollection AddModulithTransport(
        this IServiceCollection services, TransportKind transport, string rabbitMqConnectionString)
    {
        switch (transport)
        {
            case TransportKind.InMemory:
                services.AddBareWireWithInMemory(t =>
                {
                    t.DrainTimeout(TimeSpan.FromSeconds(10));
                    ModulithMessaging.Configure(t.ConfigureTopology, t.DefaultExchange, t.ReceiveEndpoint);
                });
                break;

            case TransportKind.RabbitMQ:
                services.AddBareWireWithRabbitMq(r =>
                {
                    r.Host(rabbitMqConnectionString);
                    ModulithMessaging.Configure(r.ConfigureTopology, r.DefaultExchange, r.ReceiveEndpoint);
                });
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(transport), transport, $"Unknown transport kind '{transport}'.");
        }

        return services;
    }
}
