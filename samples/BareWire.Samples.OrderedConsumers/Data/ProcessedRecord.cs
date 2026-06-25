namespace BareWire.Samples.OrderedConsumers.Data;

/// <summary>
/// Persists a single processed message event for offline ordering verification.
/// </summary>
public sealed class ProcessedRecord
{
    /// <summary>Auto-incremented primary key.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Ordering key that was stamped in the "ordering-key" transport header.
    /// <para>
    /// Non-PII note: this sample uses synthetic, non-PII demo keys (e.g. "acct-A", "acct-B").
    /// In a production system, raw ordering keys that identify natural persons must not be
    /// stored or logged without appropriate data-governance controls.
    /// </para>
    /// </summary>
    public required string Key { get; set; }

    /// <summary>Zero-based sequence number within the key stream.</summary>
    public required int Sequence { get; set; }

    /// <summary>CLR short name of the message type (e.g. "OrderShipped", "InventoryAdjusted").</summary>
    public required string MessageType { get; set; }

    /// <summary><see cref="DateTime.UtcNow"/> ticks when the consumer processed this record.</summary>
    public required long ProcessedAtTicks { get; set; }

    /// <summary>
    /// Identity of the consumer instance that processed this record.
    /// Format: "{MachineName}-{ProcessId}" so that competing replicas can be distinguished.
    /// </summary>
    public required string InstanceId { get; set; }

    /// <summary>
    /// Opaque identifier shared by all records from a single <c>POST /events/generate</c> call.
    /// Allows the smoke test to isolate its own run's records from accumulated prior runs.
    /// </summary>
    public required string RunId { get; set; }

    /// <summary>
    /// The receive endpoint (queue) name that produced this record. Used by the smoke test
    /// to separate SAC cross-instance records (strict ordering guarantee) from LocalPartitioned
    /// records (single-instance, typed-selector lane hashing — no cross-instance guarantee).
    /// </summary>
    public required string EndpointName { get; set; }
}
