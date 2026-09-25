using BareWire.Abstractions;
using BareWire.Samples.InMemoryModularMonolith.Modules.Ordering;

using Microsoft.Extensions.Logging;

namespace BareWire.Samples.InMemoryModularMonolith.Modules.Billing;

/// <summary>
/// Consumes <see cref="OrderPlaced"/> from the fanout ordering exchange, records the payment in
/// <see cref="PaymentLedger"/>, and publishes <see cref="PaymentCaptured"/> to the topic billing
/// exchange.
/// </summary>
/// <remarks>
/// The <see cref="PaymentCaptured"/> publish below goes directly to the transport via
/// <see cref="ConsumeContext{T}"/> — <c>ConsumeContext.PublishAsync</c> is not buffered by the
/// transactional outbox. The outbox registered in <c>Program.cs</c> still applies to the inbox side:
/// it deduplicates redeliveries into this consumer and into the Shipping consumers by message id.
/// </remarks>
/// <param name="ledger">Records captured payments.</param>
/// <param name="clock">Supplies the payment's captured-at timestamp.</param>
/// <param name="logger">Logs the captured payment.</param>
public sealed partial class OrderPlacedBillingConsumer(
    PaymentLedger ledger, TimeProvider clock, ILogger<OrderPlacedBillingConsumer> logger)
    : IConsumer<OrderPlaced>
{
    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<OrderPlaced> context)
    {
        OrderPlaced message = context.Message;

        if (!ledger.TryRecord(message.OrderId, message.Amount))
        {
            // Ledger at capacity — already logged by PaymentLedger. Nothing further to do: this
            // consumer never retries locally, matching the "no unbounded state" rule.
            return;
        }

        LogPaymentCaptured(logger, message.OrderId);

        var captured = new PaymentCaptured(message.OrderId, message.Amount, clock.GetUtcNow());
        await context.PublishAsync(captured, context.CancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Billing captured payment for order {OrderId}")]
    private static partial void LogPaymentCaptured(ILogger logger, string orderId);
}
