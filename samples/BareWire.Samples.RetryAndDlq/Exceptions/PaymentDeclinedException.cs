namespace BareWire.Samples.RetryAndDlq.Exceptions;

/// <summary>
/// Thrown by <c>PaymentProcessor</c> when the simulated payment gateway declines the transaction.
/// BareWire catches this exception, applies the configured retry policy, and — after all retry
/// attempts are exhausted — the broker routes the message to the dead-letter queue via native
/// RabbitMQ DLX (x-dead-letter-exchange).
/// </summary>
public sealed class PaymentDeclinedException : Exception
{
    /// <summary>
    /// Initialises a new instance of <see cref="PaymentDeclinedException"/>
    /// with the specified payment ID.
    /// </summary>
    /// <param name="paymentId">The identifier of the declined payment.</param>
    public PaymentDeclinedException(string paymentId)
        : base($"Payment '{paymentId}' was declined by the payment gateway.")
    {
    }
}
