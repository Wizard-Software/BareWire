namespace BareWire.Samples.RawMessageInterop.Messages;

/// <summary>
/// Represents an event originating from a legacy external system, mapped from raw JSON.
/// Carries the event type discriminator, raw payload, and the identifier of the originating system.
/// </summary>
public record ExternalEvent(string EventType, string Payload, string SourceSystem);
