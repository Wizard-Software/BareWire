using BareWire.Abstractions.Saga;

namespace BareWire.Samples.SagaOrderFlow.Saga;

/// <summary>
/// Persistent state for the order SAGA, tracking the full order lifecycle from creation
/// through payment, shipment, and optional compensation on failure.
/// </summary>
/// <remarks>
/// Persisted to PostgreSQL via <see cref="BareWire.Saga.EntityFramework.EfCoreSagaRepository{TSaga}"/>.
/// Properties are intentionally mutable because EF Core materialises rows into existing instances.
/// </remarks>
public sealed class OrderSagaState : ISagaState
{
    /// <inheritdoc />
    public Guid CorrelationId { get; set; }

    /// <inheritdoc />
    public string CurrentState { get; set; } = "Initial";

    /// <inheritdoc />
    public int Version { get; set; }

    /// <summary>Gets or sets the business order identifier (string).</summary>
    public string? OrderId { get; set; }

    /// <summary>Gets or sets the monetary amount of the order.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets the shipping address for this order.</summary>
    public string? ShippingAddress { get; set; }

    /// <summary>Gets or sets the payment identifier received from the payment provider.</summary>
    public string? PaymentId { get; set; }

    /// <summary>Gets or sets the carrier tracking number assigned when the order is shipped.</summary>
    public string? TrackingNumber { get; set; }

    /// <summary>Gets or sets the reason for payment failure, populated when the order is rejected.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Gets or sets the UTC timestamp when the saga instance was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the UTC timestamp of the most recent state change.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
