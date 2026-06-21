using System.Text.Json;

namespace BareWire.Interop.MassTransit;

/// <summary>
/// DTO mapping a MassTransit JSON envelope (<c>application/vnd.masstransit+json</c>).
/// All fields are nullable for permissive parsing of foreign envelopes.
/// Unknown fields are silently ignored by System.Text.Json's default behavior.
/// </summary>
/// <param name="MessageId">Unique identifier for this message instance.</param>
/// <param name="RequestId">
/// Identifier of the request this message is a response to. Used by
/// <see cref="MassTransitEnvelopeDeserializer"/> (via <c>IResponseEnvelopeReader</c>)
/// to correlate responses to pending BareWire requests when the transport-level
/// correlation identifier is absent.
/// </param>
/// <param name="CorrelationId">Optional user-supplied correlation identifier.</param>
/// <param name="ConversationId">Optional identifier grouping a chain of related messages.</param>
/// <param name="InitiatorId">Optional identifier of the message that initiated the conversation.</param>
/// <param name="SourceAddress">Transport URI of the endpoint that sent the message.</param>
/// <param name="DestinationAddress">Transport URI of the intended recipient endpoint.</param>
/// <param name="ResponseAddress">Transport URI to which the response should be sent.</param>
/// <param name="FaultAddress">Transport URI to which fault messages should be delivered.</param>
/// <param name="MessageType">URN-formatted message type identifiers.</param>
/// <param name="SentTime">UTC timestamp when the message was sent.</param>
/// <param name="ExpirationTime">UTC deadline after which the message is considered expired.</param>
/// <param name="Headers">Optional transport-level headers.</param>
/// <param name="Message">The raw message payload as a deferred JSON element.</param>
internal sealed record MassTransitEnvelope(
    Guid? MessageId,
    Guid? RequestId,
    Guid? CorrelationId,
    Guid? ConversationId,
    Guid? InitiatorId,
    string? SourceAddress,
    string? DestinationAddress,
    string? ResponseAddress,
    string? FaultAddress,
    IReadOnlyList<string>? MessageType,
    DateTimeOffset? SentTime,
    DateTimeOffset? ExpirationTime,
    IReadOnlyDictionary<string, object?>? Headers,
    JsonElement? Message
);
