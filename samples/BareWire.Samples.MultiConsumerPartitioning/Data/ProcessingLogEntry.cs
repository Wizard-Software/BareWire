namespace BareWire.Samples.MultiConsumerPartitioning.Data;

/// <summary>
/// Records a single message processing event, capturing which consumer handled the message,
/// on which managed thread, and when. Used to verify per-partition sequential ordering.
/// </summary>
public sealed class ProcessingLogEntry
{
    /// <summary>Auto-incremented primary key.</summary>
    public int Id { get; set; }

    /// <summary>Correlation identifier shared across related events (maps to a partition).</summary>
    public required string CorrelationId { get; set; }

    /// <summary>Short name of the consumer type that processed the message (e.g. "OrderEvent").</summary>
    public required string ConsumerType { get; set; }

    /// <summary>CLR type name of the message (e.g. "OrderEvent").</summary>
    public required string MessageType { get; set; }

    /// <summary>UTC timestamp when the message was processed.</summary>
    public required DateTime ProcessedAt { get; set; }

    /// <summary>Managed thread ID on which the consumer ran.</summary>
    public required int ThreadId { get; set; }
}
