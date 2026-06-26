using BareWire.Abstractions.Exceptions;

namespace BareWire.Configuration;

/// <summary>
/// Provides fail-fast validation of a <see cref="BusConfigurator"/> before the bus starts.
/// All validation errors throw <see cref="BareWireConfigurationException"/> with a clear message
/// so that misconfiguration is caught at startup rather than at runtime.
/// </summary>
internal static class ConfigurationValidator
{
    /// <summary>
    /// Validates the supplied <paramref name="configurator"/> and throws
    /// <see cref="BareWireConfigurationException"/> on the first validation failure found.
    /// Transport presence is determined by <paramref name="transportRegistered"/>, which reflects
    /// the fact that an <c>ITransportAdapter</c> was resolved from the DI container (D5 / ADR-028),
    /// rather than by the <c>BusConfigurator.HasTransport</c> marker.
    /// </summary>
    /// <param name="configurator">The bus configurator to validate.</param>
    /// <param name="transportRegistered">
    /// <see langword="true"/> when an <c>ITransportAdapter</c> was successfully resolved from the
    /// DI container; <see langword="false"/> when no transport adapter is registered.
    /// </param>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when any required configuration is missing or invalid.
    /// </exception>
    internal static void Validate(BusConfigurator configurator, bool transportRegistered)
    {
        ArgumentNullException.ThrowIfNull(configurator);

        ValidateTransport(transportRegistered);
        ValidateReceiveEndpoints(configurator);
    }

    private static void ValidateTransport(bool transportRegistered)
    {
        if (!transportRegistered)
        {
            throw new BareWireConfigurationException(
                optionName: "Transport",
                optionValue: null,
                expectedValue: "A transport adapter must be registered. " +
                               "Call AddBareWireWithRabbitMq(...) (or another AddBareWireWith{Transport}) " +
                               "to register the transport and the core in one call, " +
                               "or register an ITransportAdapter in the DI container.");
        }
    }

    private static void ValidateReceiveEndpoints(BusConfigurator configurator)
    {
        foreach (ReceiveEndpointConfiguration endpoint in configurator.ReceiveEndpoints)
        {
            ValidateEndpointHasConsumer(endpoint);
            ValidatePrefetchCount(endpoint);
            ValidateConcurrentMessageLimit(endpoint);
        }
    }

    private static void ValidateEndpointHasConsumer(ReceiveEndpointConfiguration endpoint)
    {
        if (!endpoint.HasAnyConsumer)
        {
            throw new BareWireConfigurationException(
                optionName: $"ReceiveEndpoint[{endpoint.EndpointName}].Consumers",
                optionValue: "0",
                expectedValue: "Each receive endpoint must have at least one consumer, raw consumer, or state machine saga registered.");
        }
    }

    private static void ValidatePrefetchCount(ReceiveEndpointConfiguration endpoint)
    {
        if (endpoint.PrefetchCount <= 0)
        {
            throw new BareWireConfigurationException(
                optionName: $"ReceiveEndpoint[{endpoint.EndpointName}].PrefetchCount",
                optionValue: endpoint.PrefetchCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                expectedValue: "PrefetchCount must be greater than 0.");
        }
    }

    private static void ValidateConcurrentMessageLimit(ReceiveEndpointConfiguration endpoint)
    {
        if (endpoint.ConcurrentMessageLimit <= 0)
        {
            throw new BareWireConfigurationException(
                optionName: $"ReceiveEndpoint[{endpoint.EndpointName}].ConcurrentMessageLimit",
                optionValue: endpoint.ConcurrentMessageLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                expectedValue: "ConcurrentMessageLimit must be greater than 0.");
        }
    }
}
