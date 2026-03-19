namespace BareWire.Samples.ObservabilityShowcase.Messages;

/// <summary>
/// Published when payment for a demo order has been processed.
/// </summary>
public sealed record DemoPaymentProcessed(string OrderId, string PaymentId, DateTime ProcessedAt);
