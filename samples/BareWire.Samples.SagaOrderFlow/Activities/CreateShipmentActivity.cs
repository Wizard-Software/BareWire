using BareWire.Abstractions.Saga;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.SagaOrderFlow.Activities;

/// <summary>Arguments for <see cref="CreateShipmentActivity"/>.</summary>
public sealed record CreateShipmentArguments(string OrderId, string ShippingAddress);

/// <summary>Compensation log produced by <see cref="CreateShipmentActivity.ExecuteAsync"/>.</summary>
public sealed record CreateShipmentLog(string OrderId, string ShipmentId);

/// <summary>
/// Compensable activity that creates a shipment for an order.
/// On compensation, cancels the pending shipment.
/// </summary>
public sealed partial class CreateShipmentActivity(ILogger<CreateShipmentActivity> logger)
    : ICompensableActivity<CreateShipmentArguments, CreateShipmentLog>
{
    /// <inheritdoc />
    public Task<CreateShipmentLog> ExecuteAsync(
        CreateShipmentArguments arguments,
        CancellationToken cancellationToken = default)
    {
        string shipmentId = Guid.NewGuid().ToString();
        LogShipmentCreated(arguments.OrderId, shipmentId, arguments.ShippingAddress);
        return Task.FromResult(new CreateShipmentLog(arguments.OrderId, shipmentId));
    }

    /// <inheritdoc />
    public Task CompensateAsync(
        CreateShipmentLog log,
        CancellationToken cancellationToken = default)
    {
        LogShipmentCancelled(log.OrderId, log.ShipmentId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Shipment created for order {OrderId}: shipment {ShipmentId} to {ShippingAddress}")]
    private partial void LogShipmentCreated(string orderId, string shipmentId, string shippingAddress);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Shipment {ShipmentId} cancelled for order {OrderId} (compensation)")]
    private partial void LogShipmentCancelled(string orderId, string shipmentId);
}
