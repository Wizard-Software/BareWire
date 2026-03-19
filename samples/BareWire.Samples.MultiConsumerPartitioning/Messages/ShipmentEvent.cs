namespace BareWire.Samples.MultiConsumerPartitioning.Messages;

/// <summary>A shipment-domain event published to the <c>events</c> topic exchange with routing key <c>shipment.dispatched</c>.</summary>
public sealed record ShipmentEvent(string ShipmentId, string CorrelationId, DateTime CreatedAt);
