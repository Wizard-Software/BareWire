using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;

namespace BareWire.Transport.Kafka.Internal;

/// <summary>
/// Parsed Kafka-specific topic parameters extracted from a <see cref="QueueDeclaration"/>'s
/// <c>Arguments</c> dictionary. Used by <c>KafkaTransportAdapter.DeployTopologyAsync</c> to
/// build a <c>TopicSpecification</c> for the Confluent admin client.
/// </summary>
internal readonly record struct KafkaTopicSpec(
    int NumPartitions,
    short ReplicationFactor,
    Dictionary<string, string> Configs);

/// <summary>
/// Constant argument keys recognised by the Kafka transport adapter and parser logic
/// for extracting topic parameters from <see cref="QueueDeclaration.Arguments"/>.
/// </summary>
internal static class KafkaTopologyArguments
{
    /// <summary>Argument key for the number of partitions. Value: <c>int</c>, default: <c>1</c>.</summary>
    internal const string Partitions = "bw.kafka.partitions";

    /// <summary>
    /// Argument key for the replication factor. Value: <c>short</c>, default: <c>-1</c>
    /// (use the broker default).
    /// </summary>
    internal const string ReplicationFactor = "bw.kafka.replication-factor";

    /// <summary>
    /// Argument key for the message retention window in milliseconds.
    /// Maps to <c>Configs["retention.ms"]</c>. Value: <c>long</c>.
    /// </summary>
    internal const string RetentionMs = "bw.kafka.retention.ms";

    /// <summary>
    /// Prefix for pass-through Kafka topic config entries.
    /// A key <c>bw.kafka.config.&lt;x&gt;</c> is forwarded as <c>Configs["&lt;x&gt;"]</c>.
    /// </summary>
    internal const string ConfigPrefix = "bw.kafka.config.";

    /// <summary>
    /// Parses <see cref="QueueDeclaration.Arguments"/> into a <see cref="KafkaTopicSpec"/>.
    /// When <paramref name="queue"/> has no <c>Arguments</c>, returns defaults
    /// (1 partition, -1 replication, empty config).
    /// </summary>
    /// <param name="queue">The queue declaration to parse.</param>
    /// <returns>A <see cref="KafkaTopicSpec"/> with the extracted values.</returns>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when <c>bw.kafka.partitions</c> is less than 1, or
    /// <c>bw.kafka.replication-factor</c> is less than -1.
    /// </exception>
    internal static KafkaTopicSpec Parse(QueueDeclaration queue)
    {
        IReadOnlyDictionary<string, object>? args = queue.Arguments;

        if (args is null || args.Count == 0)
        {
            return new KafkaTopicSpec(
                NumPartitions: 1,
                ReplicationFactor: -1,
                Configs: []);
        }

        int numPartitions = 1;
        short replicationFactor = -1;
        Dictionary<string, string> configs = [];

        foreach ((string key, object value) in args)
        {
            if (key == Partitions)
            {
                numPartitions = ParseInt32(key, value);
                if (numPartitions < 1)
                {
                    throw new BareWireConfigurationException(
                        optionName: Partitions,
                        optionValue: numPartitions.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        expectedValue: "An integer >= 1");
                }
            }
            else if (key == ReplicationFactor)
            {
                replicationFactor = ParseInt16(key, value);
                if (replicationFactor < -1)
                {
                    throw new BareWireConfigurationException(
                        optionName: ReplicationFactor,
                        optionValue: replicationFactor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        expectedValue: "A short >= -1 (-1 means broker default)");
                }
            }
            else if (key == RetentionMs)
            {
                long retentionMs = ParseInt64(key, value);
                configs["retention.ms"] = retentionMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (key.StartsWith(ConfigPrefix, StringComparison.Ordinal))
            {
                string topicConfigKey = key[ConfigPrefix.Length..];
                configs[topicConfigKey] = value.ToString() ?? string.Empty;
            }
            // Unknown BW argument keys are silently ignored (forward-compatible).
        }

        return new KafkaTopicSpec(
            NumPartitions: numPartitions,
            ReplicationFactor: replicationFactor,
            Configs: configs);
    }

    private static int ParseInt32(string key, object value)
    {
        try
        {
            return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new BareWireConfigurationException(
                optionName: key,
                optionValue: value.ToString(),
                expectedValue: "A valid 32-bit integer",
                innerException: ex);
        }
    }

    private static short ParseInt16(string key, object value)
    {
        try
        {
            return Convert.ToInt16(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new BareWireConfigurationException(
                optionName: key,
                optionValue: value.ToString(),
                expectedValue: "A valid 16-bit integer (short)",
                innerException: ex);
        }
    }

    private static long ParseInt64(string key, object value)
    {
        try
        {
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new BareWireConfigurationException(
                optionName: key,
                optionValue: value.ToString(),
                expectedValue: "A valid 64-bit integer (long)",
                innerException: ex);
        }
    }
}
