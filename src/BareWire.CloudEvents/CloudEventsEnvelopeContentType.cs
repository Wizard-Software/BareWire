namespace BareWire.CloudEvents;

/// <summary>
/// Holds the MIME content type for CloudEvents structured mode (envelope) messages.
/// </summary>
/// <remarks>
/// Per the CloudEvents 1.0 HTTP Protocol Binding specification, structured content mode
/// uses the content type <c>application/cloudevents+json</c> to carry both the CE context
/// attributes and the event data in a single JSON document. This constant is used as the
/// <see cref="BareWire.Abstractions.Serialization.IMessageSerializer.ContentType"/> value
/// returned by <see cref="CloudEventsEnvelopeSerializer"/>.
/// </remarks>
internal static class CloudEventsEnvelopeContentType
{
    /// <summary>
    /// The MIME content type for CloudEvents structured mode: <c>application/cloudevents+json</c>.
    /// </summary>
    internal const string Value = "application/cloudevents+json";
}
