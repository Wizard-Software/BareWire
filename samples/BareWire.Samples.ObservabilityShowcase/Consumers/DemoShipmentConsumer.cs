using BareWire.Abstractions;
using BareWire.Samples.ObservabilityShowcase.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.ObservabilityShowcase.Consumers;

/// <summary>
/// Processes <see cref="DemoShipmentDispatched"/> events and logs pipeline completion.
/// </summary>
/// <remarks>
/// Resolved from DI per-message (transient lifetime). Final hop in the observability showcase chain:
/// DemoOrderCreated → DemoPaymentProcessed → DemoShipmentDispatched.
/// The complete end-to-end trace is visible in the Aspire Dashboard / Jaeger.
/// </remarks>
public sealed partial class DemoShipmentConsumer(ILogger<DemoShipmentConsumer> logger) : IConsumer<DemoShipmentDispatched>
{
    /// <inheritdoc />
    public Task ConsumeAsync(ConsumeContext<DemoShipmentDispatched> context)
    {
        DemoShipmentDispatched shipment = context.Message;

        LogShipmentCompleted(logger, shipment.OrderId, shipment.TrackingNumber, shipment.DispatchedAt);

        // Terminal consumer — the full pipeline is complete.
        // The distributed trace now spans: publish → order → payment → shipment.
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Pipeline complete for order {OrderId}: tracking {TrackingNumber}, dispatched at {DispatchedAt}")]
    private static partial void LogShipmentCompleted(
        ILogger logger, string orderId, string trackingNumber, DateTime dispatchedAt);
}
