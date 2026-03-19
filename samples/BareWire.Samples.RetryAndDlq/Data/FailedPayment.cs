namespace BareWire.Samples.RetryAndDlq.Data;

/// <summary>
/// Represents a payment that was moved to the dead-letter queue after all retry attempts
/// were exhausted. Persisted by <c>DlqConsumer</c>.
/// </summary>
public sealed class FailedPayment
{
    /// <summary>Gets or sets the surrogate primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the identifier of the failed payment.</summary>
    public required string PaymentId { get; set; }

    /// <summary>Gets or sets the payment amount.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets the ISO 4217 currency code (e.g. <c>USD</c>).</summary>
    public required string Currency { get; set; }

    /// <summary>Gets or sets the error message that caused the failure.</summary>
    public required string Error { get; set; }

    /// <summary>Gets or sets the UTC timestamp when the failure was recorded.</summary>
    public DateTime FailedAt { get; set; }
}
