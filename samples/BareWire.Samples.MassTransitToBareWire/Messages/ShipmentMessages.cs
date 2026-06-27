namespace BareWire.Samples.MassTransitToBareWire.Messages;

/// <summary>
/// Raw fire-and-forget command handled by the <c>raw</c> consumer in this sample.
///
/// <para>
/// Unlike <see cref="CheckInventory"/> (which arrives wrapped in a MassTransit envelope),
/// <c>ShipmentNotice</c> is published by BareWire's own <c>IBus.PublishAsync&lt;ShipmentNotice&gt;</c>
/// as plain JSON (<c>application/json</c>) with no envelope — the raw-first default format.
/// It carries a <c>BW-MessageType</c> header so the dispatcher routes it to
/// <c>ShipmentConsumer</c> on the shared queue without any per-consumer envelope opt-in.
/// </para>
/// </summary>
public record ShipmentNotice(string Sku, int Quantity);

/// <summary>
/// Raw event emitted by <c>ShipmentConsumer</c> after it has processed a
/// <see cref="ShipmentNotice"/>. Published via <c>IBus.PublishAsync&lt;ShipmentRecorded&gt;</c>
/// to an observable topic exchange so the end-to-end smoke test can confirm the raw
/// consumer ran (raw-first JSON, no envelope) on the same queue as the envelope consumer.
/// </summary>
public record ShipmentRecorded(string Sku, int Quantity, string ProcessedBy);
