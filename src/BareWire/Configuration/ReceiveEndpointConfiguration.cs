using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;

namespace BareWire.Configuration;

/// <summary>
/// Stores all configuration data collected through <see cref="IReceiveEndpointConfigurator"/>
/// for a single named receive endpoint. Consumed by <see cref="ConfigurationValidator"/>
/// and by the bus startup logic when wiring consumers to transport queues.
/// </summary>
internal sealed class ReceiveEndpointConfiguration : IReceiveEndpointConfigurator
{
    private readonly List<Type> _consumerTypes = [];
    private readonly List<ConsumerRegistration> _consumerRegistrations = [];
    private readonly List<Type> _rawConsumerTypes = [];
    private readonly List<Type> _sagaTypes = [];

    internal ReceiveEndpointConfiguration(string endpointName)
    {
        ArgumentNullException.ThrowIfNull(endpointName);
        EndpointName = endpointName;
    }

    // ── Configuration properties ───────────────────────────────────────────────

    internal string EndpointName { get; }

    internal IReadOnlyList<Type> ConsumerTypes => _consumerTypes;

    /// <summary>
    /// Gets the materialized consumer registrations for this endpoint — one per <c>Consumer&lt;,&gt;</c>
    /// call — carrying the consumer/message types plus the accumulated routing-key set and the
    /// <c>AcceptUntyped</c> flag. Consumed by the consume loop to select consumers at dispatch time.
    /// </summary>
    internal IReadOnlyList<ConsumerRegistration> ConsumerRegistrations => _consumerRegistrations;

    internal IReadOnlyList<Type> RawConsumerTypes => _rawConsumerTypes;
    internal IReadOnlyList<Type> SagaTypes => _sagaTypes;

    internal Type? SerializerOverrideType { get; private set; }
    internal Type? DeserializerOverrideType { get; private set; }

    /// <summary>
    /// Gets the per-key consumer-ordering configuration for this endpoint, or <see langword="null"/>
    /// when no <c>OrderedBy</c> call was made (per-key ordering OFF — the default).
    /// </summary>
    internal ConsumerOrderingConfiguration? Ordering { get; private set; }

    internal bool HasAnyConsumer =>
        _consumerTypes.Count > 0 || _rawConsumerTypes.Count > 0 || _sagaTypes.Count > 0;

    // ── IReceiveEndpointConfigurator ───────────────────────────────────────────

    /// <inheritdoc />
    public int PrefetchCount { get; set; } = 16;

    /// <inheritdoc />
    public int ConcurrentMessageLimit { get; set; } = 8;

    /// <inheritdoc />
    public bool ConfigureConsumeTopology { get; set; }

    /// <inheritdoc />
    public string? DefaultContentType { get; set; }

    /// <inheritdoc />
    public RawSerializerOptions RawSerializerOptions { get; set; } = RawSerializerOptions.None;

    /// <inheritdoc />
    public int RetryCount { get; set; }

    /// <inheritdoc />
    public TimeSpan RetryInterval { get; set; } = TimeSpan.Zero;

    /// <inheritdoc />
    public void Consumer<TConsumer, TMessage>()
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
    {
        _consumerTypes.Add(typeof(TConsumer));
        _consumerRegistrations.Add(new ConsumerRegistration(typeof(TConsumer), typeof(TMessage)));
    }

    /// <inheritdoc />
    public void Consumer<TConsumer, TMessage>(Action<IConsumerConfigurator<TConsumer, TMessage>> configure)
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(configure);

        ConsumerConfigurator<TConsumer, TMessage> configurator = new();
        configure(configurator);

        _consumerTypes.Add(typeof(TConsumer));
        _consumerRegistrations.Add(configurator.Build());
    }

    /// <inheritdoc />
    public void RawConsumer<T>() where T : class, IRawConsumer
    {
        _rawConsumerTypes.Add(typeof(T));
    }

    /// <inheritdoc />
    public void StateMachineSaga<TSaga>() where TSaga : class
    {
        _sagaTypes.Add(typeof(TSaga));
    }

    /// <inheritdoc />
    public void UseSerializer<TSerializer>() where TSerializer : class, IMessageSerializer
    {
        SerializerOverrideType = typeof(TSerializer);
    }

    /// <inheritdoc />
    public void UseDeserializer<TDeserializer>() where TDeserializer : class, IMessageDeserializer
    {
        DeserializerOverrideType = typeof(TDeserializer);
    }

    /// <inheritdoc />
    public void OrderedBy<TMessage>(Func<TMessage, object?> selector) where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(selector);
        var configuration = new ConsumerOrderingConfiguration();
        configuration.By(selector);
        Ordering = configuration;
    }

    /// <inheritdoc />
    public void OrderedByHeader(string headerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(headerName);
        var configuration = new ConsumerOrderingConfiguration();
        configuration.ByHeader(headerName);
        Ordering = configuration;
    }

    /// <inheritdoc />
    public void OrderedBy(Action<IConsumerOrderingConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var configuration = new ConsumerOrderingConfiguration();
        configure(configuration);
        Ordering = configuration;
    }
}
