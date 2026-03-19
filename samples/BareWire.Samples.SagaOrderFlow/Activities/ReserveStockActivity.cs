using BareWire.Abstractions.Saga;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.SagaOrderFlow.Activities;

/// <summary>Arguments for <see cref="ReserveStockActivity"/>.</summary>
public sealed record ReserveStockArguments(string OrderId, decimal Amount);

/// <summary>Compensation log produced by <see cref="ReserveStockActivity.ExecuteAsync"/>.</summary>
public sealed record ReserveStockLog(string OrderId, string ReservationId);

/// <summary>
/// Compensable activity that reserves stock for an order.
/// On compensation, releases the stock reservation.
/// </summary>
public sealed partial class ReserveStockActivity(ILogger<ReserveStockActivity> logger)
    : ICompensableActivity<ReserveStockArguments, ReserveStockLog>
{
    /// <inheritdoc />
    public Task<ReserveStockLog> ExecuteAsync(
        ReserveStockArguments arguments,
        CancellationToken cancellationToken = default)
    {
        string reservationId = Guid.NewGuid().ToString();
        LogStockReserved(arguments.OrderId, reservationId, arguments.Amount);
        return Task.FromResult(new ReserveStockLog(arguments.OrderId, reservationId));
    }

    /// <inheritdoc />
    public Task CompensateAsync(
        ReserveStockLog log,
        CancellationToken cancellationToken = default)
    {
        LogStockReleased(log.OrderId, log.ReservationId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Stock reserved for order {OrderId}: reservation {ReservationId}, amount {Amount}")]
    private partial void LogStockReserved(string orderId, string reservationId, decimal amount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Stock reservation {ReservationId} released for order {OrderId} (compensation)")]
    private partial void LogStockReleased(string orderId, string reservationId);
}
