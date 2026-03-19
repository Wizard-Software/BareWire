using BareWire.Abstractions.Saga;

namespace BareWire.Samples.RabbitMQ.Saga;

/// <summary>
/// Persistent state for the order SAGA, tracking the lifecycle of a single order
/// from creation through payment to completion or failure.
/// </summary>
/// <remarks>
/// Persisted to SQL Server via <see cref="BareWire.Saga.EntityFramework.EfCoreSagaRepository{TSaga}"/>.
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

    /// <summary>Gets or sets the business order identifier (string, as used in the sample API).</summary>
    public string? OrderNumber { get; set; }

    /// <summary>Gets or sets the monetary amount of the order.</summary>
    public decimal Amount { get; set; }

    /// <summary>Gets or sets the ISO-4217 currency code for the order (e.g. <c>"USD"</c>).</summary>
    public string? Currency { get; set; }
}
