namespace BareWire.Samples.OrderedConsumers.Messages;

/// <summary>
/// Emitted when inventory is adjusted for an account. Carries an ordering key for per-account
/// sequential delivery across competing consumer instances.
/// </summary>
public sealed record InventoryAdjusted(
    /// <summary>Ordering key used by the transactional outbox and consumer endpoint (header: "ordering-key").</summary>
    string OrderingKey,
    /// <summary>Account identifier — same value as <see cref="OrderingKey"/> for this demo.</summary>
    string AccountId,
    /// <summary>Zero-based sequence number within the key stream; used for strict-order assertion.</summary>
    int Sequence,
    /// <summary>Unique adjustment identifier.</summary>
    string AdjustmentId,
    /// <summary>UTC timestamp when the inventory adjustment occurred.</summary>
    DateTime OccurredAt);
