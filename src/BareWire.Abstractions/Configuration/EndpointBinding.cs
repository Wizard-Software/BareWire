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
    public bool HasDeadLetterExchange { get; init; }

    /// <summary>Gets the optional per-endpoint serializer type override. Null means use global.</summary>
    public Type? SerializerOverrideType { get; init; }

    /// <summary>Gets the optional per-endpoint deserializer type override. Null means use global.</summary>
    public Type? DeserializerOverrideType { get; init; }
}
