namespace BareWire.Samples.MassTransitToBareWire.Messages;

/// <summary>
/// Request: MassTransit asks BareWire to check the available inventory for a SKU.
/// Sent via MassTransit IRequestClient&lt;T&gt; in the MassTransit JSON envelope format.
/// </summary>
public record CheckInventory(string Sku);

/// <summary>
/// Response: BareWire returns the inventory level for the requested SKU.
/// The reply is routed back to MassTransit via the MT JSON envelope's responseAddress.
/// </summary>
public record InventoryLevel(string Sku, int Available, string ProcessedBy);

/// <summary>
/// Event (past tense): BareWire emits this domain event after it has processed a
/// <see cref="CheckInventory"/> request. It is published via <c>IBus.PublishAsync&lt;InventoryChecked&gt;</c>
/// and routed by the ergonomic per-type publish mapping configured in <c>Program.cs</c> —
/// landing on the <c>bw-inventory-events</c> topic exchange with routing key <c>inventory.checked</c>.
/// This is the message type that exercises the ergonomic <c>DeclareExchange&lt;T&gt;</c> /
/// <c>Publish&lt;T&gt;</c> routing showcased by this sample.
/// </summary>
public record InventoryChecked(string Sku, int Available, string ProcessedBy);
