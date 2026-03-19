using BareWire.Abstractions;
using BareWire.Samples.ObservabilityShowcase.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.ObservabilityShowcase.Consumers;

/// <summary>
/// Processes <see cref="DemoPaymentProcessed"/> events and publishes <see cref="DemoShipmentDispatched"/>
/// to continue the distributed trace pipeline.
/// </summary>
/// <remarks>
/// Resolved from DI per-message (transient lifetime). Second hop in the observability showcase chain:
/// DemoOrderCreated → DemoPaymentProcessed → DemoShipmentDispatched.
/// </remarks>
public sealed partial class DemoPaymentConsumer(ILogger<DemoPaymentConsumer> logger) : IConsumer<DemoPaymentProcessed>
{
    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<DemoPaymentProcessed> context)
    {
        DemoPaymentProcessed payment = context.Message;

        LogProcessingPayment(logger, payment.OrderId, payment.PaymentId);

        await Task.Delay(millisecondsDelay: 0, context.CancellationToken).ConfigureAwait(false);

        // Publish the final event in the chain — the DemoShipmentConsumer receives this
        // on the "demo-shipments" queue and completes the distributed trace.
        await context.PublishAsync(
            new DemoShipmentDispatched(
                payment.OrderId,
                TrackingNumber: $"track-{Guid.NewGuid():N}",
                DispatchedAt: DateTime.UtcNow),
            context.CancellationToken).ConfigureAwait(false);

        LogShipmentPublished(logger, payment.OrderId);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Processing demo payment {PaymentId} for order {OrderId}")]
    private static partial void LogProcessingPayment(ILogger logger, string orderId, string paymentId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "DemoShipmentDispatched published for order {OrderId}")]
    private static partial void LogShipmentPublished(ILogger logger, string orderId);
}
