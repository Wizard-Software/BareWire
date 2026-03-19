namespace BareWire.Samples.RawMessageInterop.Data;

/// <summary>
/// Represents a message that has been received and processed by one of the interop consumers.
/// </summary>
public sealed class ProcessedMessage
{
    /// <summary>Gets or sets the surrogate primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the event type discriminator from the legacy system.</summary>
    public required string EventType { get; set; }

    /// <summary>Gets or sets the raw payload string from the legacy message.</summary>
    public required string Payload { get; set; }

    /// <summary>Gets or sets the source system identifier.</summary>
    public required string SourceSystem { get; set; }

    /// <summary>Gets or sets the correlation identifier extracted from the message header.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Gets or sets the UTC timestamp when this message was processed.</summary>
    public DateTimeOffset ProcessedAt { get; set; }

    /// <summary>
    /// Gets or sets the consumer type that processed this message.
    /// Either <c>"Raw"</c> (processed by <c>RawEventConsumer</c>) or
    /// <c>"Typed"</c> (processed by <c>TypedEventConsumer</c>).
    /// </summary>
    public required string ConsumerType { get; set; }
}
