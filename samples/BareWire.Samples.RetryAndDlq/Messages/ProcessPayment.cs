namespace BareWire.Samples.RetryAndDlq.Messages;

/// <summary>
/// Command to process a payment. Published to the <c>payments</c> exchange.
/// Intentionally fails 70% of the time (see <c>PaymentProcessor</c>) to demonstrate
/// retry-then-DLQ behaviour.
/// </summary>
public sealed record ProcessPayment(string PaymentId, decimal Amount, string Currency);
