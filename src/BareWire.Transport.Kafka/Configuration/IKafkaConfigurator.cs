using Confluent.Kafka;

namespace BareWire.Transport.Kafka.Configuration;

/// <summary>
/// Provides a fluent API for configuring the Kafka transport adapter.
/// Obtained via <see cref="ServiceCollectionExtensions.AddBareWireKafka"/>.
/// </summary>
/// <remarks>
/// R1.1: producer side. R1.2: consumer side added
/// (<see cref="ConsumerGroup"/>, <see cref="ConsumerAutoOffsetReset"/>,
/// <see cref="ConsumerPartitionAssignmentStrategy"/>).
/// Topology configuration will be added in R1.4.
/// </remarks>
public interface IKafkaConfigurator
{
    /// <summary>
    /// Configures the Kafka bootstrap server(s) to connect to.
    /// Must be called before the bus is started.
    /// </summary>
    /// <param name="bootstrapServers">
    /// A comma-separated list of host:port pairs (e.g. <c>localhost:9092</c>).
    /// Must not be <see langword="null"/> or empty.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="bootstrapServers"/> is <see langword="null"/> or empty.
    /// </exception>
    void BootstrapServers(string bootstrapServers);

    /// <summary>
    /// Sets the consumer group identifier. Required when using
    /// <c>ITransportAdapter.ConsumeAsync</c>.
    /// </summary>
    /// <param name="groupId">
    /// The consumer group id. Must not be <see langword="null"/> or empty.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="groupId"/> is <see langword="null"/> or empty.
    /// </exception>
    void ConsumerGroup(string groupId);

    /// <summary>
    /// Sets the offset reset policy applied when the consumer group has no committed offset.
    /// Defaults to <see cref="AutoOffsetReset.Earliest"/> when not called.
    /// </summary>
    /// <param name="autoOffsetReset">The <see cref="AutoOffsetReset"/> policy to apply.</param>
    void ConsumerAutoOffsetReset(AutoOffsetReset autoOffsetReset);

    /// <summary>
    /// Sets the partition assignment strategy for the consumer group.
    /// Defaults to <see cref="KafkaPartitionAssignmentStrategy.CooperativeSticky"/> when not called.
    /// </summary>
    /// <param name="strategy">The <see cref="KafkaPartitionAssignmentStrategy"/> to apply.</param>
    void ConsumerPartitionAssignmentStrategy(KafkaPartitionAssignmentStrategy strategy);

    /// <summary>
    /// Configures the retry-topic + DLQ-topic pattern (R1.3, ADR-010). The pattern is opt-in —
    /// call <see cref="IKafkaRetryDlqConfigurator.Enable"/> inside <paramref name="configure"/> to
    /// activate it. When not configured (or not enabled), <c>SettleAsync(Defer)</c> throws
    /// <see cref="NotSupportedException"/> and <c>SettleAsync(Reject)</c> logs and does not store
    /// the offset (R1.2 behaviour).
    /// </summary>
    /// <param name="configure">A delegate that configures the pattern via <see cref="IKafkaRetryDlqConfigurator"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    void ConfigureRetryDlq(Action<IKafkaRetryDlqConfigurator> configure);
}
