namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Transport-agnostic description of a receive endpoint: queue name, prefetch settings,
/// and the consumer registrations that should be dispatched for inbound messages.
/// Registered in DI by the transport package and consumed by the core bus startup.
/// </summary>
public sealed class EndpointBinding
{
    /// <summary>Gets the transport queue / endpoint name to consume from.</summary>
    public required string EndpointName { get; init; }

    /// <summary>Gets the prefetch count that maps to <see cref="FlowControlOptions.MaxInFlightMessages"/>.</summary>
    public int PrefetchCount { get; init; } = 16;

    /// <summary>
    /// Gets the cross-key concurrency cap (number of parallel dispatch lanes) for the local partitioned
    /// dispatch layer. This becomes load-bearing only when <see cref="Ordering"/> is non-null (per-key
    /// ordering ON); with per-key ordering OFF the consume pump stays strictly sequential and this value
    /// has no effect (pre-per-key-ordering behavior is preserved byte-for-byte).
    /// </summary>
    public int ConcurrentMessageLimit { get; init; } = 8;

    /// <summary>
    /// Gets the per-key consumer-ordering configuration for this endpoint, or <see langword="null"/> when
    /// no <c>OrderedBy</c> call was made (per-key ordering OFF — the default). Exposed as the shared
    /// read-only <see cref="IConsumerOrderingConfiguration"/> so the transport-agnostic dispatch engine
    /// reads it without downcasting to a transport-local carrier type.
    /// </summary>
    public IConsumerOrderingConfiguration? Ordering { get; init; }

    /// <summary>Gets the consumer registrations for this endpoint.</summary>
    public IReadOnlyList<ConsumerRegistration> Consumers { get; init; } = [];

    /// <summary>Gets the raw consumer types registered on this endpoint.</summary>
    public IReadOnlyList<Type> RawConsumers { get; init; } = [];

    /// <summary>Gets the saga state machine types registered on this endpoint.</summary>
    public IReadOnlyList<Type> SagaTypes { get; init; } = [];

    /// <summary>Gets the number of retry attempts for failed message processing. Zero means no retry.</summary>
    public int RetryCount { get; init; }

    /// <summary>Gets the interval between retry attempts.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gets a value indicating whether the underlying queue has a dead-letter exchange configured.
    /// When <see langword="false"/> and a message is NACKed, the message will be permanently lost.
    /// This value is set by the transport based on declared topology and may not reflect
    /// broker-side configuration made outside the application.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="DeadLetterExchange"/>: <see langword="true"/> when
    /// <see cref="DeadLetterExchange"/> is non-null and non-empty. Kept for backward compatibility.
    /// </remarks>
    public bool HasDeadLetterExchange => DeadLetterExchange is { Length: > 0 };

    /// <summary>
    /// Gets the name of the dead-letter exchange configured on the underlying queue via the
    /// <c>x-dead-letter-exchange</c> queue argument, or <see langword="null"/> when no DLX is
    /// configured. Used by the per-key poison contract (R8.12) to route parked messages durably.
    /// </summary>
    public string? DeadLetterExchange { get; init; }

    /// <summary>
    /// Gets the routing key used when publishing to <see cref="DeadLetterExchange"/>, derived from
    /// the <c>x-dead-letter-routing-key</c> queue argument. When <see langword="null"/>, the
    /// endpoint name (queue name) is used as the routing key — matching RabbitMQ DLX default
    /// semantics (the original routing key is preserved when no explicit DLX routing key is set).
    /// </summary>
    public string? DeadLetterRoutingKey { get; init; }

    /// <summary>Gets the optional per-endpoint serializer type override. Null means use global.</summary>
    public Type? SerializerOverrideType { get; init; }

    /// <summary>Gets the optional per-endpoint deserializer type override. Null means use global.</summary>
    public Type? DeserializerOverrideType { get; init; }

    /// <summary>
    /// Creates a copy of this <see cref="EndpointBinding"/> with <see cref="Consumers"/> replaced by
    /// <paramref name="consumers"/> and every other <see langword="init"/>-only property preserved
    /// unchanged. Used by the core's start-up <c>ConsumerDefinition&lt;TConsumer&gt;</c> discovery to
    /// materialize merged consumer registrations without a <c>record</c>-style <c>with</c> expression
    /// (this type is a plain <see langword="sealed class"/>, not a <see langword="record"/>).
    /// </summary>
    /// <param name="consumers">The replacement consumer registrations for this endpoint.</param>
    /// <returns>A new <see cref="EndpointBinding"/> instance carrying <paramref name="consumers"/>.</returns>
    internal EndpointBinding WithConsumers(IReadOnlyList<ConsumerRegistration> consumers) => new()
    {
        EndpointName = EndpointName,
        PrefetchCount = PrefetchCount,
        ConcurrentMessageLimit = ConcurrentMessageLimit,
        Ordering = Ordering,
        Consumers = consumers,
        RawConsumers = RawConsumers,
        SagaTypes = SagaTypes,
        RetryCount = RetryCount,
        RetryInterval = RetryInterval,
        DeadLetterExchange = DeadLetterExchange,
        DeadLetterRoutingKey = DeadLetterRoutingKey,
        SerializerOverrideType = SerializerOverrideType,
        DeserializerOverrideType = DeserializerOverrideType,
    };
}
