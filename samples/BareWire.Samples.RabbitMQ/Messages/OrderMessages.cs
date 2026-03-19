namespace BareWire.Samples.RabbitMQ.Messages;

// ADR-001: Raw-first — plain records, no envelope, no base class, no attributes.
// Event naming follows CONSTITUTION.md: past tense for events.

/// <summary>Event raised when a new order is submitted by the customer.</summary>
public sealed record OrderCreated(string OrderId, decimal Amount, string Currency);

/// <summary>Event raised when an order has been processed by the <c>OrderConsumer</c>.</summary>
public sealed record OrderProcessed(string OrderId, string Status);

/// <summary>Event raised when a payment for an order is successfully received.</summary>
public sealed record PaymentReceived(string OrderId, decimal Amount);

/// <summary>Event raised when a payment for an order fails.</summary>
public sealed record PaymentFailed(string OrderId, string Reason);
