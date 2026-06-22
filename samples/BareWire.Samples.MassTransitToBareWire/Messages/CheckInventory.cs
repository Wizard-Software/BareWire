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
