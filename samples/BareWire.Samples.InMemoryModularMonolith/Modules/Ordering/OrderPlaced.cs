namespace BareWire.Samples.InMemoryModularMonolith.Modules.Ordering;

/// <summary>
/// Published by <see cref="OrderingModule.PlaceOrderAsync"/> to the fanout ordering exchange. Consumed
/// by both the Billing module (to capture payment) and the Shipping module (to mark the order received).
/// </summary>
/// <param name="OrderId">The generated order identifier.</param>
/// <param name="CustomerId">The customer who placed the order.</param>
/// <param name="Amount">The order amount.</param>
/// <param name="PlacedAt">The time the order was placed.</param>
public sealed record OrderPlaced(string OrderId, string CustomerId, decimal Amount, DateTimeOffset PlacedAt);
