using BareWire.Abstractions.Serialization;

namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Configures a single receive endpoint (queue consumer) on the bus.
/// Passed to the delegate in <see cref="IBus.ConnectReceiveEndpoint"/> and to
/// static endpoint configuration during bus setup.
/// Per ADR-002, <see cref="ConfigureConsumeTopology"/> defaults to <see langword="false"/> —
/// topology must be declared and deployed explicitly.
/// </summary>
public interface IReceiveEndpointConfigurator
{
    /// <summary>
    /// Gets or sets the number of messages the broker will deliver to this endpoint
    /// before waiting for acknowledgements. Controls consumer throughput vs. fairness.
    /// </summary>
    int PrefetchCount { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of messages that can be processed concurrently
    /// by this endpoint's consumer handlers.
    /// </summary>
    int ConcurrentMessageLimit { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bus should automatically declare and bind
    /// transport topology (exchanges, queues) for this endpoint's consumers.
    /// Defaults to <see langword="false"/> per ADR-002 (manual topology).
    /// </summary>
    bool ConfigureConsumeTopology { get; set; }

    /// <summary>
    /// Gets or sets the default MIME content type assumed for messages that arrive without
    /// an explicit content-type header (e.g. <c>"application/json"</c>).
    /// <see langword="null"/> means the serializer is selected by the transport header only.
    /// </summary>
    string? DefaultContentType { get; set; }

    /// <summary>
    /// Gets or sets the raw serializer behaviour flags for messages on this endpoint.
    /// </summary>
    RawSerializerOptions RawSerializerOptions { get; set; }

    /// <summary>
    /// Gets or sets the number of times a failed message will be retried before being moved
    /// to the dead-letter queue or fault endpoint. Defaults to <c>0</c> (no retries).
    /// </summary>
    int RetryCount { get; set; }

    /// <summary>
    /// Gets or sets the delay between retry attempts. Defaults to <see cref="TimeSpan.Zero"/>
    /// (immediate retry).
    /// </summary>
    TimeSpan RetryInterval { get; set; }

    /// <summary>
    /// Registers a typed consumer <typeparamref name="TConsumer"/> that processes messages of type
    /// <typeparamref name="TMessage"/>. The consumer is resolved from the DI container per message.
    /// </summary>
    /// <typeparam name="TConsumer">
    /// The consumer implementation type. Must implement <see cref="IConsumer{TMessage}"/>.
    /// </typeparam>
    /// <typeparam name="TMessage">The message type this consumer handles. Must be a reference type.</typeparam>
    void Consumer<TConsumer, TMessage>()
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class;

    /// <summary>
    /// Registers a raw consumer <typeparamref name="T"/> that receives undeserialized byte payloads.
    /// The consumer is resolved from the DI container per message.
    /// </summary>
    /// <typeparam name="T">The raw consumer implementation type. Must implement <see cref="IRawConsumer"/>.</typeparam>
    void RawConsumer<T>() where T : class, IRawConsumer;

    /// <summary>
    /// Registers a state machine saga of type <typeparamref name="TSaga"/> on this endpoint.
    /// The saga type and its instance store are resolved from the DI container.
    /// </summary>
    /// <typeparam name="TSaga">The saga state machine type. Must be a reference type.</typeparam>
    void StateMachineSaga<TSaga>() where TSaga : class;

    /// <summary>
    /// Overrides the serializer for this endpoint. The serializer type is resolved from DI.
    /// When not set, the global bus-level serializer is used.
    /// </summary>
    /// <typeparam name="TSerializer">The serializer type to use for this endpoint.</typeparam>
    void UseSerializer<TSerializer>() where TSerializer : class, IMessageSerializer;

    /// <summary>
    /// Overrides the deserializer for this endpoint. The deserializer type is resolved from DI.
    /// When not set, the global bus-level deserializer resolver is used.
    /// </summary>
    /// <typeparam name="TDeserializer">The deserializer type to use for this endpoint.</typeparam>
    void UseDeserializer<TDeserializer>() where TDeserializer : class, IMessageDeserializer;
}
