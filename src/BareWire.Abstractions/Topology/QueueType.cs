namespace BareWire.Abstractions.Topology;

/// <summary>
/// Specifies the RabbitMQ queue type, controlling replication and storage characteristics.
/// </summary>
public enum QueueType
{
    /// <summary>Classic non-replicated queue (RabbitMQ default).</summary>
    Classic,

    /// <summary>Raft-based replicated queue for high availability. Recommended for production.</summary>
    Quorum,

    /// <summary>Append-only log-based queue for high-throughput streaming workloads.</summary>
    Stream,
}
