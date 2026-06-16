namespace BareWire.CloudEvents;

/// <summary>
/// Holds the diagnostic content-type label used when identifying CloudEvents binary-mode
/// messages in exception messages and validation context.
/// </summary>
/// <remarks>
/// Per ADR-007 §R1, the binary mode binding maps CE context attributes to transport headers
/// (<c>ce-*</c> prefix, e.g. <c>BasicProperties.Headers</c> on RabbitMQ/AMQP 0-9-1). This is
/// NOT a certified AMQP 1.0 CloudEvents binding. The payload is carried raw without an envelope
/// (ADR-001); the content-type value here is a diagnostic label used in
/// <see cref="BareWire.Abstractions.Exceptions.BareWireSerializationException"/> messages only.
/// </remarks>
internal static class CloudEventsBinaryContentType
{
    /// <summary>
    /// The diagnostic content-type label for CloudEvents binary mode.
    /// Used only in validation exception messages — the actual payload is raw (ADR-001/ADR-007).
    /// </summary>
    internal const string Value = "application/cloudevents+binary";
}
