using BareWire.Abstractions;
using BareWire.Samples.InMemoryModularMonolith.Modules.Billing;

using Microsoft.Extensions.Logging;

namespace BareWire.Samples.InMemoryModularMonolith.Modules.Shipping;

/// <summary>
/// Consumes <see cref="PaymentCaptured"/> from the topic billing exchange and marks the order's
/// payment confirmed on the <see cref="ShipmentBoard"/>.
/// </summary>
/// <param name="board">The Shipping module's state.</param>
/// <param name="logger">Logs the confirmed payment.</param>
public sealed partial class PaymentCapturedShippingConsumer(
    ShipmentBoard board, ILogger<PaymentCapturedShippingConsumer> logger)
    : IConsumer<PaymentCaptured>
{
    /// <inheritdoc />
    public Task ConsumeAsync(ConsumeContext<PaymentCaptured> context)
    {
        string orderId = context.Message.OrderId;
        board.MarkPaymentConfirmed(orderId);
        LogPaymentConfirmed(logger, orderId);

        if (board.GetStatus(orderId) == ShipmentStatus.ReadyToShip)
        {
            LogReadyToShip(logger, orderId);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shipping confirmed payment for order {OrderId}")]
    private static partial void LogPaymentConfirmed(ILogger logger, string orderId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shipping marked order {OrderId} ReadyToShip")]
    private static partial void LogReadyToShip(ILogger logger, string orderId);
}
