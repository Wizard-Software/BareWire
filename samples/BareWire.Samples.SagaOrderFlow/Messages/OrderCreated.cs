namespace BareWire.Samples.SagaOrderFlow.Messages;

/// <summary>Raised when a new order is placed by a customer.</summary>
public sealed record OrderCreated(string OrderId, decimal Amount, string ShippingAddress);
