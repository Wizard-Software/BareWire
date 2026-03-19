namespace BareWire.Samples.SagaOrderFlow.Messages;

/// <summary>Raised when payment for an order cannot be processed.</summary>
public sealed record PaymentFailed(string OrderId, string Reason);
