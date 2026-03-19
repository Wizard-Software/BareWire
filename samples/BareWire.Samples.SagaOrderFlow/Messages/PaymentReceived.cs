namespace BareWire.Samples.SagaOrderFlow.Messages;

/// <summary>Raised when payment for an order is successfully received.</summary>
public sealed record PaymentReceived(string OrderId, string PaymentId);
