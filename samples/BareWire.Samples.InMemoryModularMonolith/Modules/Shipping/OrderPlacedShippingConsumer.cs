using BareWire.Abstractions;
using BareWire.Samples.InMemoryModularMonolith.Modules.Ordering;

using Microsoft.Extensions.Logging;

namespace BareWire.Samples.InMemoryModularMonolith.Modules.Shipping;

/// <summary>
/// Consumes <see cref="OrderPlaced"/> from the fanout ordering exchange and marks the order received
/// on the <see cref="ShipmentBoard"/>.
/// </summary>
/// <param name="board">The Shipping module's state.</param>
/// <param name="logger">Logs the received order.</param>
public sealed partial class OrderPlacedShippingConsumer(
    ShipmentBoard board, ILogger<OrderPlacedShippingConsumer> logger)
    : IConsumer<OrderPlaced>
{
    /// <inheritdoc />
    public Task ConsumeAsync(ConsumeContext<OrderPlaced> context)
    {
        string orderId = context.Message.OrderId;
        board.MarkOrderReceived(orderId);
        LogOrderReceived(logger, orderId);

        if (board.GetStatus(orderId) == ShipmentStatus.ReadyToShip)
        {
            LogReadyToShip(logger, orderId);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shipping received OrderPlaced for order {OrderId}")]
    private static partial void LogOrderReceived(ILogger logger, string orderId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shipping marked order {OrderId} ReadyToShip")]
    private static partial void LogReadyToShip(ILogger logger, string orderId);
}
