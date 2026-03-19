using BareWire.Abstractions;
using BareWire.Samples.ObservabilityShowcase.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.ObservabilityShowcase.Consumers;

/// <summary>
/// Processes <see cref="DemoOrderCreated"/> events and publishes <see cref="DemoPaymentProcessed"/>
/// to continue the distributed trace pipeline.
/// </summary>
/// <remarks>
/// Resolved from DI per-message (transient lifetime). Part of the observability showcase chain:
/// DemoOrderCreated → DemoPaymentProcessed → DemoShipmentDispatched.
/// Each hop is recorded as a child span in the distributed trace.
/// </remarks>
public sealed partial class DemoOrderConsumer(ILogger<DemoOrderConsumer> logger) : IConsumer<DemoOrderCreated>
{
    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<DemoOrderCreated> context)
    {
        DemoOrderCreated order = context.Message;

        LogProcessingOrder(logger, order.OrderId, order.Amount);

        // Simulate lightweight processing — the delay is intentionally zero so the
        // trace spans remain tight while still demonstrating the async pipeline.
        await Task.Delay(millisecondsDelay: 0, context.CancellationToken).ConfigureAwait(false);

        // Publish the next event in the chain — the DemoPaymentConsumer receives this
        // on the "demo-payments" queue and continues the distributed trace.
        await context.PublishAsync(
            new DemoPaymentProcessed(
                order.OrderId,
                PaymentId: $"pay-{Guid.NewGuid():N}",
                ProcessedAt: DateTime.UtcNow),
            context.CancellationToken).ConfigureAwait(false);

        LogPaymentPublished(logger, order.OrderId);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Processing demo order {OrderId} for amount {Amount}")]
    private static partial void LogProcessingOrder(ILogger logger, string orderId, decimal amount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "DemoPaymentProcessed published for order {OrderId}")]
    private static partial void LogPaymentPublished(ILogger logger, string orderId);
}
