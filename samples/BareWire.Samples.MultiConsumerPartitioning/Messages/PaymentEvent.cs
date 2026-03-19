namespace BareWire.Samples.MultiConsumerPartitioning.Messages;

/// <summary>A payment-domain event published to the <c>events</c> topic exchange with routing key <c>payment.processed</c>.</summary>
public sealed record PaymentEvent(string PaymentId, string CorrelationId, DateTime CreatedAt);
