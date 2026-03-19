using BareWire.Abstractions.Saga;

namespace BareWire.IntegrationTests.Saga;

/// <summary>
/// SAGA state type used in end-to-end integration tests for the Order saga.
/// Persisted to SQLite via <see cref="BareWire.Saga.EntityFramework.EfCoreSagaRepository{TSaga}"/>.
/// </summary>
public sealed class OrderSagaState : ISagaState
{
    /// <inheritdoc />
    public Guid CorrelationId { get; set; }

    /// <inheritdoc />
    public string CurrentState { get; set; } = "Initial";

    /// <inheritdoc />
    public int Version { get; set; }

    /// <summary>Gets or sets the business order number associated with this saga instance.</summary>
    public string? OrderNumber { get; set; }

    /// <summary>Gets or sets the monetary amount of the order.</summary>
    public decimal Amount { get; set; }
}
