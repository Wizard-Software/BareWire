namespace BareWire.Samples.MassTransitRequestResponse.Messages;

/// <summary>
/// Request: a BareWire client asks for an order's status.
/// Sent by BareWire in the MassTransit envelope format (application/vnd.masstransit+json).
/// </summary>
public record CheckOrderStatus(string OrderId);

/// <summary>
/// Response: the MassTransit responder returns the current order status.
/// Received by BareWire after the MassTransit envelope is decoded.
/// </summary>
public record OrderStatus(string OrderId, string Status, string ProcessedBy);
