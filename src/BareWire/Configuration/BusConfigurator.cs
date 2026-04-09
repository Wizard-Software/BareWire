using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Serialization;

namespace BareWire.Configuration;

/// <summary>
/// Internal implementation of <see cref="IBusConfigurator"/> that collects all configuration
/// supplied by the application before the bus is built and started. Consumed by
/// <see cref="ConfigurationValidator"/> (fail-fast validation) and by
/// <c>ServiceCollectionExtensions.AddBareWire</c> (DI registration).
/// </summary>
internal sealed class BusConfigurator : IBusConfigurator
{
    private readonly List<Type> _middlewareTypes = [];
    private readonly List<ReceiveEndpointConfiguration> _receiveEndpoints = [];
    private readonly Dictionary<Type, Type> _serializerMappings = [];

    // ── Collected configuration ────────────────────────────────────────────────

    internal IReadOnlyList<Type> MiddlewareTypes => _middlewareTypes;
    internal IReadOnlyList<ReceiveEndpointConfiguration> ReceiveEndpoints => _receiveEndpoints;
    internal IReadOnlyDictionary<Type, Type> SerializerMappings => _serializerMappings;

    /// <summary>
    /// Gets the registered serializer type, or <see langword="null"/> when no serializer was configured.
    /// </summary>
    internal Type? SerializerType { get; private set; }

    /// <summary>
    /// Gets the RabbitMQ configurator delegate, or <see langword="null"/> when RabbitMQ was not configured.
    /// </summary>
    internal Action<IRabbitMqConfigurator>? RabbitMqConfigurator { get; private set; }

    /// <summary>
    /// Gets the observability configurator delegate, or <see langword="null"/> when not configured.
    /// Placeholder for Phase 6 (Observability).
    /// </summary>
    internal Action<object>? ObservabilityConfigurator { get; private set; }

    /// <summary>
    /// Gets a value indicating whether an InMemory transport has been registered via DI
    /// (used by tests and by <see cref="ConfigurationValidator"/> to determine whether
    /// a transport is available without requiring a full transport package reference).
    /// </summary>
    internal bool HasInMemoryTransport { get; set; }

    // ── IBusConfigurator ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public void UseRabbitMQ(Action<IRabbitMqConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        RabbitMqConfigurator = configure;
    }

    /// <inheritdoc />
    public void ConfigureObservability(Action<object> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ObservabilityConfigurator = configure;
    }

    /// <inheritdoc />
    public void AddMiddleware<T>() where T : class, IMessageMiddleware
    {
        _middlewareTypes.Add(typeof(T));
    }

    /// <inheritdoc />
    public void UseSerializer<T>() where T : class, IMessageSerializer
    {
        SerializerType = typeof(T);
    }

    /// <inheritdoc />
    public void MapSerializer<TMessage, TSerializer>()
        where TMessage : class
        where TSerializer : class, IMessageSerializer
    {
        _serializerMappings[typeof(TMessage)] = typeof(TSerializer);
    }

    // ── Endpoint configuration ─────────────────────────────────────────────────

    /// <summary>
    /// Adds a receive endpoint configuration for the given queue name.
    /// The <paramref name="configure"/> delegate populates the endpoint settings.
    /// </summary>
    /// <param name="endpointName">The queue / endpoint name on the transport.</param>
    /// <param name="configure">Delegate invoked with the endpoint configurator to apply settings.</param>
    internal void AddReceiveEndpoint(string endpointName, Action<IReceiveEndpointConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(endpointName);
        ArgumentNullException.ThrowIfNull(configure);

        ReceiveEndpointConfiguration endpoint = new(endpointName);
        configure(endpoint);
        _receiveEndpoints.Add(endpoint);
    }

    // ── Transport detection ────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when at least one transport has been configured —
    /// either RabbitMQ (Phase 3) or InMemory (set externally before validation).
    /// </summary>
    internal bool HasTransport => RabbitMqConfigurator is not null || HasInMemoryTransport;
}
