namespace BareWire.Samples.ObservabilityShowcase.Messages;

/// <summary>
/// Published when a shipment for a demo order has been dispatched.
/// </summary>
public sealed record DemoShipmentDispatched(string OrderId, string TrackingNumber, DateTime DispatchedAt);
