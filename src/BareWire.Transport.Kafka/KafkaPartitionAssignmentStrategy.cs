namespace BareWire.Transport.Kafka;

/// <summary>
/// Specifies the partition assignment strategy used by a Kafka consumer group.
/// Maps to <see cref="Confluent.Kafka.PartitionAssignmentStrategy"/> via
/// <see cref="Internal.PartitionAssignmentStrategyResolver"/>.
/// </summary>
public enum KafkaPartitionAssignmentStrategy
{
    /// <summary>
    /// Assigns partitions by range within each topic.
    /// The standard strategy, but causes full rebalances when group membership changes.
    /// Corresponds to <see cref="Confluent.Kafka.PartitionAssignmentStrategy.Range"/>.
    /// </summary>
    Range,

    /// <summary>
    /// Assigns partitions in a round-robin fashion across all consumers in the group.
    /// Corresponds to <see cref="Confluent.Kafka.PartitionAssignmentStrategy.RoundRobin"/>.
    /// </summary>
    RoundRobin,

    /// <summary>
    /// Performs incremental cooperative rebalancing — only the partitions that need to move
    /// are revoked, minimising stop-the-world pauses. Recommended for new applications.
    /// Default value (D9). Corresponds to
    /// <see cref="Confluent.Kafka.PartitionAssignmentStrategy.CooperativeSticky"/>.
    /// </summary>
    CooperativeSticky,
}
