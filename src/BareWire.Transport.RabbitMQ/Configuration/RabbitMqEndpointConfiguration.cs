using System.Reflection;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;

namespace BareWire.Transport.RabbitMQ.Configuration;

internal sealed class RabbitMqEndpointConfiguration : IReceiveEndpointConfigurator
{
    private readonly List<Type> _consumerTypes = [];
    private readonly List<ConsumerRegistration> _consumerRegistrations = [];
    private readonly List<Type> _rawConsumerTypes = [];
    private readonly List<Type> _sagaTypes = [];

    internal RabbitMqEndpointConfiguration(string queueName)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        QueueName = queueName;
    }

    internal string QueueName { get; }

    internal IReadOnlyList<Type> ConsumerTypes => _consumerTypes;
    internal IReadOnlyList<ConsumerRegistration> ConsumerRegistrations => _consumerRegistrations;
    internal IReadOnlyList<Type> RawConsumerTypes => _rawConsumerTypes;
    internal IReadOnlyList<Type> SagaTypes => _sagaTypes;

    internal Type? SerializerOverrideType { get; private set; }
    internal Type? DeserializerOverrideType { get; private set; }

    /// <summary>
    /// Gets the per-key consumer-ordering configuration for this endpoint, or <see langword="null"/>
    /// when no <c>OrderedBy</c> call was made (per-key ordering OFF — the default). Captured here only to
    /// satisfy the <see cref="IReceiveEndpointConfigurator"/> contract; the RabbitMQ transport-native
    /// ordering layer consumes it in a later task of the per-key ordering feature.
    /// </summary>
    internal OrderingConfiguration? Ordering { get; private set; }

    // ── IReceiveEndpointConfigurator ───────────────────────────────────────────

    public int PrefetchCount { get; set; } = 16;

    public int ConcurrentMessageLimit { get; set; } = 8;

    public bool ConfigureConsumeTopology { get; set; }

    public string? DefaultContentType { get; set; }

    public RawSerializerOptions RawSerializerOptions { get; set; } = RawSerializerOptions.None;

    public int RetryCount { get; set; }

    public TimeSpan RetryInterval { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// The open generic definition of the parameterless <see cref="Consumer{TConsumer, TMessage}()"/>
    /// overload, cached once and closed via <see cref="MethodInfo.MakeGenericMethod"/> at startup by the
    /// single-<see cref="IConsumer{T}"/> sugar overload — never per message.
    /// </summary>
    private static readonly MethodInfo TypedConsumerMethod = typeof(RabbitMqEndpointConfiguration)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(static m =>
            m.Name == nameof(Consumer)
            && m.IsGenericMethodDefinition
            && m.GetGenericArguments().Length == 2
            && m.GetParameters().Length == 0);

    /// <inheritdoc />
    public void Consumer<TConsumer>()
        where TConsumer : class
    {
        // Infer TMessage from the consumer's single IConsumer<T> (fail-fast on none/multiple), then bake
        // the closed Consumer<TConsumer, TMessage>() delegate ONCE at startup and delegate to it — mirrors
        // the core configurator; the dispatch hot path stays reflection-free (ADR-003).
        Type messageType = ConsumerMessageTypeInference.ResolveSingleMessageType(typeof(TConsumer));
        TypedConsumerMethod.MakeGenericMethod(typeof(TConsumer), messageType).Invoke(this, null);
    }

    public void Consumer<TConsumer, TMessage>()
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
    {
        _consumerTypes.Add(typeof(TConsumer));
        _consumerRegistrations.Add(new ConsumerRegistration(typeof(TConsumer), typeof(TMessage)));
    }

    /// <summary>
    /// Registers a typed consumer and configures its consume-time routing-key dispatch via the grouped
    /// <paramref name="configure"/> block. The configurator accumulates the consumer's set of AMQP topic
    /// patterns and the secure-by-default <c>AcceptUntyped</c> opt-in, materialized into a
    /// <see cref="ConsumerRegistration"/> with identical semantics to the core endpoint configuration
    /// (per-project configurator; ADR-030 mandates the overload in both implementations).
    /// </summary>
    /// <param name="configure">The configuration block applied to this consumer's routing-key settings.</param>
    /// <typeparam name="TConsumer">The consumer implementation type.</typeparam>
    /// <typeparam name="TMessage">The message type the consumer handles.</typeparam>
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

    public void RawConsumer<T>() where T : class, IRawConsumer
    {
        _rawConsumerTypes.Add(typeof(T));
    }

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
        var configuration = new OrderingConfiguration();
        configuration.By(selector);
        Ordering = configuration;
    }

    /// <inheritdoc />
    public void OrderedByHeader(string headerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(headerName);
        var configuration = new OrderingConfiguration();
        configuration.ByHeader(headerName);
        Ordering = configuration;
    }

    /// <inheritdoc />
    public void OrderedBy(Action<IConsumerOrderingConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var configuration = new OrderingConfiguration();
        configure(configuration);
        Ordering = configuration;
    }

    /// <summary>
    /// Minimal package-local carrier capturing per-key ordering settings declared on a RabbitMQ endpoint.
    /// Required only to satisfy the <see cref="IConsumerOrderingConfigurator"/> block overload (CS0535);
    /// it performs no runtime dispatch or key resolution. The RabbitMQ package cannot reference the Core
    /// carrier (package dependency rule: Transport.RabbitMQ depends on Abstractions only), so the storage
    /// is duplicated minimally here.
    /// </summary>
    /// <remarks>
    /// Implements the read-only <see cref="IConsumerOrderingConfiguration"/> (via explicit interface
    /// implementation) so the transport-agnostic dispatch engine reads the settings from
    /// <see cref="EndpointBinding.Ordering"/> without downcasting to this package-local type — the carrier
    /// the engine sees is the shared Abstractions interface, never this concrete class.
    /// </remarks>
    internal sealed class OrderingConfiguration : IConsumerOrderingConfigurator, IConsumerOrderingConfiguration
    {
        internal string? HeaderName { get; private set; }

        internal Delegate? Selector { get; private set; }

        internal Type? SelectorMessageType { get; private set; }

        internal bool UseCorrelationId { get; private set; }

        internal int? Concurrency_ { get; private set; }

        internal ConsumerOrderingStrategy Strategy_ { get; private set; } = ConsumerOrderingStrategy.Auto;

        internal Abstractions.Configuration.TransportAffinity TransportAffinity_ { get; private set; }
            = Abstractions.Configuration.TransportAffinity.None;

        internal int MaxDeliveryAttempts_ { get; private set; }

        public IConsumerOrderingConfigurator ByHeader(string headerName)
        {
            ArgumentException.ThrowIfNullOrEmpty(headerName);
            HeaderName = headerName;
            return this;
        }

        public IConsumerOrderingConfigurator By<TMessage>(Func<TMessage, object?> selector)
            where TMessage : class
        {
            ArgumentNullException.ThrowIfNull(selector);
            Selector = selector;
            SelectorMessageType = typeof(TMessage);
            return this;
        }

        public IConsumerOrderingConfigurator ByCorrelationId()
        {
            UseCorrelationId = true;
            return this;
        }

        public IConsumerOrderingConfigurator Concurrency(int degree)
        {
            Concurrency_ = degree;
            return this;
        }

        public IConsumerOrderingConfigurator Strategy(ConsumerOrderingStrategy strategy)
        {
            Strategy_ = strategy;
            return this;
        }

        public IConsumerOrderingConfigurator TransportAffinity(TransportAffinity affinity)
        {
            TransportAffinity_ = affinity;
            return this;
        }

        public IConsumerOrderingConfigurator MaxDeliveryAttempts(int attempts)
        {
            MaxDeliveryAttempts_ = attempts;
            return this;
        }

        // ── IConsumerOrderingConfiguration (read-only view; explicit to avoid name clashes) ──────

        string? IConsumerOrderingConfiguration.HeaderName => HeaderName;

        Delegate? IConsumerOrderingConfiguration.Selector => Selector;

        Type? IConsumerOrderingConfiguration.SelectorMessageType => SelectorMessageType;

        bool IConsumerOrderingConfiguration.UseCorrelationId => UseCorrelationId;

        int? IConsumerOrderingConfiguration.Concurrency => Concurrency_;

        ConsumerOrderingStrategy IConsumerOrderingConfiguration.Strategy => Strategy_;

        Abstractions.Configuration.TransportAffinity IConsumerOrderingConfiguration.TransportAffinity => TransportAffinity_;

        int IConsumerOrderingConfiguration.MaxDeliveryAttempts => MaxDeliveryAttempts_;
    }
}
