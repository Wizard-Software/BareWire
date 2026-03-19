namespace BareWire.Samples.SagaOrderFlow.Messages;

/// <summary>Raised when the shipment for an order is dispatched by the warehouse.</summary>
public sealed record ShipmentDispatched(string OrderId, string TrackingNumber);
