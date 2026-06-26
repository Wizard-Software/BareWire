namespace BareWire.Samples.CompetingResponders.Messages;

/// <summary>
/// Request message broadcast by the requester to all competing responder replicas
/// via the per-type fanout exchange.
/// </summary>
internal sealed record PingRequest(string Payload);

/// <summary>
/// Response sent back by the first responder to win the race (first-in-wins).
/// Carries the original <see cref="Payload"/> echoed back and the identity of the
/// responder instance that answered.
/// </summary>
internal sealed record PingResponse(string Echo, string ResponderId);
