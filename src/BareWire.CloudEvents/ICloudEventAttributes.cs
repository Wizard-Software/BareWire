namespace BareWire.CloudEvents;

/// <summary>
/// Represents the context attributes defined by the CloudEvents 1.0 specification.
/// Provides a typed, read-only view of all standard CE attributes carried by a message,
/// whether extracted from inbound transport headers or constructed by a publisher.
/// </summary>
/// <remarks>
/// CloudEvents 1.0 defines four mandatory attributes (<see cref="Id"/>, <see cref="Source"/>,
/// <see cref="SpecVersion"/>, <see cref="Type"/>) and four optional ones. Consumers read these
/// attributes via <c>CloudEventContextExtensions.GetCloudEvent</c>; publishers construct an
/// implementing instance (e.g. <see cref="CloudEventContext"/>) and pass it to the publish pipeline.
/// </remarks>
public interface ICloudEventAttributes
{
    /// <summary>
    /// Gets the unique identifier of the CloudEvent.
    /// Mandatory (CE 1.0) — fail-fast validation in 13.3.
    /// </summary>
    /// <remarks>
    /// The value must be non-empty and unique within the scope of the producer
    /// (combined with <see cref="Source"/>). Consumers MUST NOT assume any specific format.
    /// </remarks>
    string Id { get; }

    /// <summary>
    /// Gets the URI identifying the context in which the event occurred.
    /// Mandatory (CE 1.0) — fail-fast validation in 13.3.
    /// </summary>
    /// <remarks>
    /// The combination of <see cref="Source"/> and <see cref="Id"/> MUST be globally unique.
    /// The value is an absolute or relative URI as defined by RFC 3986.
    /// </remarks>
    Uri Source { get; }

    /// <summary>
    /// Gets the version of the CloudEvents specification used by this event.
    /// Mandatory (CE 1.0) — fail-fast validation in 13.3.
    /// </summary>
    /// <remarks>
    /// Currently the only valid value is <c>"1.0"</c>. This attribute enables
    /// consumers to detect and reject future incompatible versions.
    /// </remarks>
    string SpecVersion { get; }

    /// <summary>
    /// Gets the type descriptor of the event, describing the kind of occurrence
    /// that caused the event to be produced.
    /// Mandatory (CE 1.0) — fail-fast validation in 13.3.
    /// </summary>
    /// <remarks>
    /// By convention, the value should be in reverse-DNS notation (e.g.
    /// <c>"com.example.order.created"</c>). Must be non-empty.
    /// </remarks>
    string Type { get; }

    /// <summary>
    /// Gets an optional, additional qualifier for the event with respect to the context
    /// expressed by <see cref="Source"/>. <see langword="null"/> when not specified by the producer.
    /// </summary>
    string? Subject { get; }

    /// <summary>
    /// Gets the optional timestamp of when the occurrence that triggered the event happened,
    /// in RFC 3339 / ISO 8601 format. <see langword="null"/> when not specified by the producer.
    /// </summary>
    DateTimeOffset? Time { get; }

    /// <summary>
    /// Gets the optional content type of the <c>data</c> value carried by the event
    /// (e.g. <c>"application/json"</c>). <see langword="null"/> when the data is not present
    /// or the type is implied by context.
    /// </summary>
    string? DataContentType { get; }

    /// <summary>
    /// Gets the optional URI that identifies the schema that the event data adheres to.
    /// <see langword="null"/> when no data schema is declared.
    /// </summary>
    Uri? DataSchema { get; }

    /// <summary>
    /// Gets the collection of CloudEvents extension attributes present on the event.
    /// Extension attribute names use the <c>ce-</c> header prefix on the transport layer.
    /// Never <see langword="null"/> — an empty dictionary is returned when no extensions are present.
    /// </summary>
    IReadOnlyDictionary<string, string> Extensions { get; }
}
