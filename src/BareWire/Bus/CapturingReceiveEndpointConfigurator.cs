using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;

namespace BareWire.Bus;

/// <summary>
/// The <see cref="IReceiveEndpointConfigurator"/> passed as the <c>endpoint</c> argument when the core's
/// start-up discovery invokes a <c>ConsumerDefinition&lt;TConsumer&gt;</c>'s <c>Configure</c> method. It
/// <strong>captures</strong> the endpoint-level settings a definition applies through that argument and lets
/// <see cref="ConsumerDefinitionDiscovery"/> materialize them back into the owning
/// <see cref="EndpointBinding"/> — so those settings are honoured rather than silently dropped.
/// </summary>
/// <remarks>
/// <para>
/// Settings that map cleanly onto <see cref="EndpointBinding"/> are recorded: <see cref="PrefetchCount"/>,
/// <see cref="ConcurrentMessageLimit"/>, <see cref="RetryCount"/>, <see cref="RetryInterval"/>, and the
/// per-endpoint serializer/deserializer overrides (<see cref="UseSerializer{TSerializer}"/> /
/// <see cref="UseDeserializer{TDeserializer}"/>). The getters are seeded from the owning endpoint so a
/// definition reads the endpoint's current value before overriding it; <see cref="IsDirty"/> flips only when
/// a setting is actually changed.
/// </para>
/// <para>
/// Settings a per-consumer definition cannot meaningfully express — registering additional consumers, raw
/// consumers or sagas (<see cref="Consumer{TConsumer}()"/> and friends), per-endpoint ordering
/// (<c>OrderedBy</c>), the consume-topology toggle, the default content type and the raw-serializer options —
/// throw <see cref="NotSupportedException"/> with actionable guidance instead of being silently discarded.
/// </para>
/// </remarks>
internal sealed class CapturingReceiveEndpointConfigurator : IReceiveEndpointConfigurator
{
    private int _prefetchCount;
    private int _concurrentMessageLimit;
    private int _retryCount;
    private TimeSpan _retryInterval;

    /// <summary>Seeds the capturer from the owning endpoint (or the <see cref="EndpointBinding"/> defaults when none).</summary>
    /// <param name="seed">The endpoint whose current values the getters return before any override.</param>
    public CapturingReceiveEndpointConfigurator(EndpointBinding? seed = null)
    {
        _prefetchCount = seed?.PrefetchCount ?? 16;
        _concurrentMessageLimit = seed?.ConcurrentMessageLimit ?? 8;
        _retryCount = seed?.RetryCount ?? 0;
        _retryInterval = seed?.RetryInterval ?? TimeSpan.Zero;
        CapturedSerializerOverrideType = seed?.SerializerOverrideType;
        CapturedDeserializerOverrideType = seed?.DeserializerOverrideType;
    }

    /// <summary>Gets a value indicating whether any endpoint-level setting was actually changed by a definition.</summary>
    internal bool IsDirty { get; private set; }

    /// <summary>Gets the captured per-endpoint serializer override type, or <see langword="null"/> when unset.</summary>
    internal Type? CapturedSerializerOverrideType { get; private set; }

    /// <summary>Gets the captured per-endpoint deserializer override type, or <see langword="null"/> when unset.</summary>
    internal Type? CapturedDeserializerOverrideType { get; private set; }

    /// <inheritdoc />
    public int PrefetchCount
    {
        get => _prefetchCount;
        set { _prefetchCount = value; IsDirty = true; }
    }

    /// <inheritdoc />
    public int ConcurrentMessageLimit
    {
        get => _concurrentMessageLimit;
        set { _concurrentMessageLimit = value; IsDirty = true; }
    }

    /// <inheritdoc />
    public int RetryCount
    {
        get => _retryCount;
        set { _retryCount = value; IsDirty = true; }
    }

    /// <inheritdoc />
    public TimeSpan RetryInterval
    {
        get => _retryInterval;
        set { _retryInterval = value; IsDirty = true; }
    }

    /// <inheritdoc />
    public bool ConfigureConsumeTopology
    {
        get => false;
        set => throw Unsupported(nameof(ConfigureConsumeTopology));
    }

    /// <inheritdoc />
    public string? DefaultContentType
    {
        get => null;
        set => throw Unsupported(nameof(DefaultContentType));
    }

    /// <inheritdoc />
    public RawSerializerOptions RawSerializerOptions
    {
        get => default;
        set => throw Unsupported(nameof(RawSerializerOptions));
    }

    /// <inheritdoc />
    public void UseSerializer<TSerializer>() where TSerializer : class, IMessageSerializer
    {
        CapturedSerializerOverrideType = typeof(TSerializer);
        IsDirty = true;
    }

    /// <inheritdoc />
    public void UseDeserializer<TDeserializer>() where TDeserializer : class, IMessageDeserializer
    {
        CapturedDeserializerOverrideType = typeof(TDeserializer);
        IsDirty = true;
    }

    /// <inheritdoc />
    public void Consumer<TConsumer>() where TConsumer : class => throw Unsupported("Consumer<TConsumer>()");

    /// <inheritdoc />
    public void Consumer<TConsumer, TMessage>()
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
        => throw Unsupported("Consumer<TConsumer, TMessage>()");

    /// <inheritdoc />
    public void Consumer<TConsumer, TMessage>(Action<IConsumerConfigurator<TConsumer, TMessage>> configure)
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
        => throw Unsupported("Consumer<TConsumer, TMessage>(...)");

    /// <inheritdoc />
    public void RawConsumer<T>() where T : class, IRawConsumer => throw Unsupported("RawConsumer<T>()");

    /// <inheritdoc />
    public void StateMachineSaga<TSaga>() where TSaga : class => throw Unsupported("StateMachineSaga<TSaga>()");

    /// <inheritdoc />
    public void OrderedBy<TMessage>(Func<TMessage, object?> selector) where TMessage : class
        => throw Unsupported("OrderedBy<TMessage>(...)");

    /// <inheritdoc />
    public void OrderedByHeader(string headerName) => throw Unsupported("OrderedByHeader(...)");

    /// <inheritdoc />
    public void OrderedBy(Action<IConsumerOrderingConfigurator> configure) => throw Unsupported("OrderedBy(...)");

    private static NotSupportedException Unsupported(string member) => new(
        $"'{member}' cannot be configured from ConsumerDefinition<T>.Configure — it is an endpoint-level " +
        "registration/setting that a per-consumer definition cannot express. Configure it directly on the " +
        "receive endpoint instead. A definition may set: PrefetchCount, ConcurrentMessageLimit, RetryCount, " +
        "RetryInterval, UseSerializer/UseDeserializer, plus the per-consumer settings on " +
        "IConsumerConfigurator<T> (routing keys, AcceptUntyped, UseMassTransitEnvelope, Retry).");
}
