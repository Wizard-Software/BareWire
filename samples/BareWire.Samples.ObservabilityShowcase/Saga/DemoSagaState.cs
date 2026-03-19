using BareWire.Abstractions.Saga;

namespace BareWire.Samples.ObservabilityShowcase.Saga;

/// <summary>
/// Persistent state for the demo observability SAGA, tracking the lifecycle of a single
/// demo order through the three-stage pipeline (order → payment → shipment).
/// </summary>
/// <remarks>
/// Persisted via <c>EfCoreSagaRepository&lt;DemoSagaState&gt;</c> with PostgreSQL.
/// Properties are intentionally mutable because EF Core materialises rows into existing instances.
/// </remarks>
public sealed class DemoSagaState : ISagaState
{
    /// <inheritdoc />
    public Guid CorrelationId { get; set; }

    /// <inheritdoc />
    public string CurrentState { get; set; } = "Initial";

    /// <inheritdoc />
    public int Version { get; set; }

    /// <summary>Gets or sets the business order identifier.</summary>
    public string? OrderId { get; set; }

    /// <summary>Gets or sets the monetary amount of the demo order.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets when the demo order was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets when the saga state was last updated.</summary>
    public DateTime UpdatedAt { get; set; }
}
