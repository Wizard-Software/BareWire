namespace BareWire.Samples.InMemoryModularMonolith.Modules.Billing;

/// <summary>
/// Published by <see cref="OrderPlacedBillingConsumer"/> to the topic billing exchange after recording
/// the payment. Consumed by the Shipping module's <c>PaymentCapturedShippingConsumer</c>.
/// </summary>
/// <param name="OrderId">The order the payment was captured for.</param>
/// <param name="Amount">The captured amount.</param>
/// <param name="CapturedAt">The time the payment was captured.</param>
public sealed record PaymentCaptured(string OrderId, decimal Amount, DateTimeOffset CapturedAt);
