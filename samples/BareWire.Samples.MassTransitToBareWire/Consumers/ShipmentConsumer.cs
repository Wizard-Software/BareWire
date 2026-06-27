using BareWire.Abstractions;
using BareWire.Samples.MassTransitToBareWire.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.MassTransitToBareWire.Consumers;

/// <summary>
/// The <c>raw</c> consumer in this sample's mixed-consumer demo.
///
/// <para>
/// This consumer is registered on the SAME receive endpoint (and the same queue) as
/// <see cref="InventoryConsumer"/>, but — unlike it — does NOT opt into the MassTransit
/// envelope. It therefore uses BareWire's default raw-first format: the inbound
/// <see cref="ShipmentNotice"/> arrives as plain JSON (<c>application/json</c>) with a
/// <c>BW-MessageType</c> header, and the dispatcher routes it here by message type.
/// </para>
///
/// <para>
/// After processing, it publishes a raw <see cref="ShipmentRecorded"/> event through
/// BareWire's <see cref="IBus"/> to an observable topic exchange, then signals
/// <see cref="ShipmentSignal"/> so the driver knows the async raw round has completed.
/// </para>
/// </summary>
internal sealed partial class ShipmentConsumer : IConsumer<ShipmentNotice>
{
    private readonly IBus _bus;
    private readonly ShipmentSignal _signal;
    private readonly ILogger<ShipmentConsumer> _logger;

    public ShipmentConsumer(IBus bus, ShipmentSignal signal, ILogger<ShipmentConsumer> logger)
    {
        _bus = bus;
        _signal = signal;
        _logger = logger;
    }

    public async Task ConsumeAsync(ConsumeContext<ShipmentNotice> context)
    {
        ShipmentNotice notice = context.Message;
        Log.ReceivedRaw(_logger, notice.Sku, notice.Quantity);

        // Emit a raw domain event so the round is externally observable. No exchange/routing key
        // at the call site — the per-type mapping configured in Program.cs routes ShipmentRecorded
        // to the observable topic exchange with routing key "shipment.recorded".
        await _bus.PublishAsync(
            new ShipmentRecorded(notice.Sku, notice.Quantity, ProcessedBy: "BareWire/ShipmentConsumer"),
            context.CancellationToken);

        Log.RecordedRaw(_logger, notice.Sku);

        // Release the driver's bounded wait — the async raw round is now complete.
        _signal.MarkRecorded();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Raw consumer received ShipmentNotice for SKU={Sku}, Quantity={Quantity} (no MassTransit envelope)")]
        internal static partial void ReceivedRaw(ILogger logger, string sku, int quantity);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Raw consumer published ShipmentRecorded for SKU={Sku}")]
        internal static partial void RecordedRaw(ILogger logger, string sku);
    }
}
