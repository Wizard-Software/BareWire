namespace BareWire.Samples.MultiConsumerPartitioning.Messages;

/// <summary>An order-domain event published to the <c>events</c> topic exchange with routing key <c>order.created</c>.</summary>
public sealed record OrderEvent(string OrderId, string CorrelationId, DateTime CreatedAt);
