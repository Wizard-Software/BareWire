using System.Text.Json;

namespace BareWire.Interop.MassTransit;

/// <summary>
/// DTO odwzorowujące kopertę JSON MassTransit (application/vnd.masstransit+json).
/// Wszystkie pola nullable — permisywne parsowanie obcych kopert.
/// Nieznane pola (host, faultAddress, requestId, responseAddress) ignorowane
/// przez domyślne zachowanie System.Text.Json.
/// </summary>
internal sealed record MassTransitEnvelope(
    Guid? MessageId,
    Guid? CorrelationId,
    Guid? ConversationId,
    Guid? InitiatorId,
    string? SourceAddress,
    string? DestinationAddress,
    IReadOnlyList<string>? MessageType,
    DateTimeOffset? SentTime,
    DateTimeOffset? ExpirationTime,
    IReadOnlyDictionary<string, object?>? Headers,
    JsonElement? Message
);
