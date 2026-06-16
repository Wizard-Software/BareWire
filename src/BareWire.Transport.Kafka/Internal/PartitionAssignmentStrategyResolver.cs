using Confluent.Kafka;

namespace BareWire.Transport.Kafka.Internal;

/// <summary>
/// Maps BareWire's <see cref="KafkaPartitionAssignmentStrategy"/> enum to the
/// Confluent.Kafka <see cref="PartitionAssignmentStrategy"/> enum used when building
/// the <c>ConsumerConfig</c>.
/// </summary>
internal static class PartitionAssignmentStrategyResolver
{
    /// <summary>
    /// Resolves a BareWire partition assignment strategy to its Confluent.Kafka counterpart.
    /// </summary>
    /// <param name="strategy">The BareWire strategy value to resolve.</param>
    /// <returns>The corresponding <see cref="PartitionAssignmentStrategy"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="strategy"/> is not a defined
    /// <see cref="KafkaPartitionAssignmentStrategy"/> value.
    /// </exception>
    internal static PartitionAssignmentStrategy Resolve(KafkaPartitionAssignmentStrategy strategy) =>
        strategy switch
        {
            KafkaPartitionAssignmentStrategy.Range           => PartitionAssignmentStrategy.Range,
            KafkaPartitionAssignmentStrategy.RoundRobin      => PartitionAssignmentStrategy.RoundRobin,
            KafkaPartitionAssignmentStrategy.CooperativeSticky => PartitionAssignmentStrategy.CooperativeSticky,
            _ => throw new ArgumentOutOfRangeException(
                     nameof(strategy),
                     strategy,
                     $"Unknown KafkaPartitionAssignmentStrategy value: {strategy}."),
        };
}
