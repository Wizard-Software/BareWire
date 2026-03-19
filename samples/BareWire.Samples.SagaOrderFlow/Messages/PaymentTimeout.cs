namespace BareWire.Samples.SagaOrderFlow.Messages;

/// <summary>Scheduled message sent when payment is not received within the allowed window.</summary>
public sealed record PaymentTimeout(string OrderId);
