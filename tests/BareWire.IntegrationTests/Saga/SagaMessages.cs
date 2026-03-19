namespace BareWire.IntegrationTests.Saga;

/// <summary>Event raised when a new order is created.</summary>
public sealed record OrderCreated(Guid OrderId, string OrderNumber, decimal Amount);

/// <summary>Event raised when payment for an order is successfully received.</summary>
public sealed record PaymentReceived(Guid OrderId);

/// <summary>Event raised when payment for an order fails.</summary>
public sealed record PaymentFailed(Guid OrderId, string Reason);

/// <summary>Event published by the saga when an order reaches the completed state.</summary>
public sealed record OrderCompleted(Guid OrderId);

/// <summary>Event used to trigger a saga timeout for an order (scheduled timeout scenario).</summary>
public sealed record OrderTimeout(Guid OrderId);
