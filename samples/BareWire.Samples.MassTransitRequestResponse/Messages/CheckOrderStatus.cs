namespace BareWire.Samples.MassTransitRequestResponse.Messages;

/// <summary>
/// Request: BareWire klient pyta o status zamówienia.
/// Wysyłany przez BareWire w formacie koperty MassTransit (application/vnd.masstransit+json).
/// </summary>
public record CheckOrderStatus(string OrderId);

/// <summary>
/// Odpowiedź: MassTransit responder zwraca aktualny status zamówienia.
/// Odbierana przez BareWire po zdekodowaniu koperty MassTransit.
/// </summary>
public record OrderStatus(string OrderId, string Status, string ProcessedBy);
