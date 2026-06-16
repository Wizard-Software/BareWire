namespace BareWire.Samples.CloudEventsInterop.Messages;

/// <summary>
/// Zdarzenie wysyłki towaru — publikowane po potwierdzeniu nadania przesyłki.
/// ADR-001: czysty rekord bez klasy bazowej ani atrybutów; nazwa w czasie przeszłym.
/// </summary>
public sealed record ShipmentDispatched(string ShipmentId, string Destination, string Carrier);

/// <summary>Ciało żądania HTTP dla endpointów publikujących przesyłkę.</summary>
public sealed record PublishShipmentRequest(string ShipmentId, string Destination, string Carrier);
