using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.Kafka.Internal;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaTopologyArgumentsTests
{
    private static QueueDeclaration QueueWithArgs(IReadOnlyDictionary<string, object>? arguments) =>
        new("test-topic", Durable: true, Exclusive: false, AutoDelete: false, Arguments: arguments);

    private static QueueDeclaration QueueWithNoArgs() =>
        new("test-topic", Durable: true, Exclusive: false, AutoDelete: false, Arguments: null);

    // ── Defaults ──────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NoArguments_ReturnsDefaults()
    {
        // Arrange
        QueueDeclaration queue = QueueWithNoArgs();

        // Act
        KafkaTopicSpec spec = KafkaTopologyArguments.Parse(queue);

        // Assert
        spec.NumPartitions.Should().Be(1);
        spec.ReplicationFactor.Should().Be(-1);
        spec.Configs.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyArguments_ReturnsDefaults()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>());

        // Act
        KafkaTopicSpec spec = KafkaTopologyArguments.Parse(queue);

        // Assert
        spec.NumPartitions.Should().Be(1);
        spec.ReplicationFactor.Should().Be(-1);
        spec.Configs.Should().BeEmpty();
    }

    // ── Partitions ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_PartitionsFromArgument_ReturnsConfiguredValue()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [KafkaTopologyArguments.Partitions] = 6,
        });

        // Act
        KafkaTopicSpec spec = KafkaTopologyArguments.Parse(queue);

        // Assert
        spec.NumPartitions.Should().Be(6);
    }

    [Fact]
    public void Parse_PartitionsAsString_ParsedCorrectly()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [KafkaTopologyArguments.Partitions] = "3",
        });

        // Act
        KafkaTopicSpec spec = KafkaTopologyArguments.Parse(queue);

        // Assert
        spec.NumPartitions.Should().Be(3);
    }

    [Fact]
    public void Parse_PartitionsLessThanOne_ThrowsBareWireConfigurationException()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [KafkaTopologyArguments.Partitions] = 0,
        });

        // Act
        Action act = () => KafkaTopologyArguments.Parse(queue);

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(KafkaTopologyArguments.Partitions);
    }

    // ── ReplicationFactor ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReplicationFactorFromArgument_ReturnsConfiguredValue()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [KafkaTopologyArguments.ReplicationFactor] = (short)3,
        });

        // Act
        KafkaTopicSpec spec = KafkaTopologyArguments.Parse(queue);

        // Assert
        spec.ReplicationFactor.Should().Be(3);
    }

    [Fact]
    public void Parse_ReplicationFactorNegativeOne_AllowedAsBrokerDefault()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [KafkaTopologyArguments.ReplicationFactor] = (short)-1,
        });

        // Act
        KafkaTopicSpec spec = KafkaTopologyArguments.Parse(queue);

        // Assert
        spec.ReplicationFactor.Should().Be(-1);
    }

    [Fact]
    public void Parse_ReplicationFactorLessThanMinusOne_ThrowsBareWireConfigurationException()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [KafkaTopologyArguments.ReplicationFactor] = (short)-2,
        });

        // Act
        Action act = () => KafkaTopologyArguments.Parse(queue);

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(KafkaTopologyArguments.ReplicationFactor);
    }

    // ── RetentionMs ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_RetentionMs_MapsToConfigsKey()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [KafkaTopologyArguments.RetentionMs] = 86400000L,
        });

        // Act
        KafkaTopicSpec spec = KafkaTopologyArguments.Parse(queue);

        // Assert
        spec.Configs.Should().ContainKey("retention.ms");
        spec.Configs["retention.ms"].Should().Be("86400000");
    }

    // ── Escape-hatch bw.kafka.config.* ────────────────────────────────────────

    [Fact]
    public void Parse_ConfigPrefixKey_PassedThroughToConfigs()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [$"{KafkaTopologyArguments.ConfigPrefix}cleanup.policy"] = "compact",
        });

        // Act
        KafkaTopicSpec spec = KafkaTopologyArguments.Parse(queue);

        // Assert
        spec.Configs.Should().ContainKey("cleanup.policy");
        spec.Configs["cleanup.policy"].Should().Be("compact");
    }

    [Fact]
    public void Parse_MultipleConfigPrefixKeys_AllPassedThrough()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [$"{KafkaTopologyArguments.ConfigPrefix}cleanup.policy"] = "compact",
            [$"{KafkaTopologyArguments.ConfigPrefix}min.insync.replicas"] = "2",
        });

        // Act
        KafkaTopicSpec spec = KafkaTopologyArguments.Parse(queue);

        // Assert
        spec.Configs.Should().ContainKeys("cleanup.policy", "min.insync.replicas");
        spec.Configs["cleanup.policy"].Should().Be("compact");
        spec.Configs["min.insync.replicas"].Should().Be("2");
    }

    // ── Configs type is concrete Dictionary<string,string> ───────────────────

    [Fact]
    public void Parse_ConfigsProperty_IsConcreteDictionary()
    {
        // Arrange — GAP-1: TopicSpecification.Configs requires Dictionary<string,string>, not IDictionary.
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [KafkaTopologyArguments.RetentionMs] = 60000L,
        });

        // Act
        KafkaTopicSpec spec = KafkaTopologyArguments.Parse(queue);

        // Assert — assignment to Dictionary<string,string> must compile (catches GAP-1 at test level too)
        Dictionary<string, string> concreteConfigs = spec.Configs;
        concreteConfigs.Should().ContainKey("retention.ms");
    }

    // ── Combined ──────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_AllArguments_ReturnsFullyPopulatedSpec()
    {
        // Arrange
        QueueDeclaration queue = QueueWithArgs(new Dictionary<string, object>
        {
            [KafkaTopologyArguments.Partitions] = 12,
            [KafkaTopologyArguments.ReplicationFactor] = (short)3,
            [KafkaTopologyArguments.RetentionMs] = 604800000L,
            [$"{KafkaTopologyArguments.ConfigPrefix}cleanup.policy"] = "delete",
        });

        // Act
        KafkaTopicSpec spec = KafkaTopologyArguments.Parse(queue);

        // Assert
        spec.NumPartitions.Should().Be(12);
        spec.ReplicationFactor.Should().Be(3);
        spec.Configs.Should().ContainKey("retention.ms");
        spec.Configs.Should().ContainKey("cleanup.policy");
        spec.Configs["retention.ms"].Should().Be("604800000");
        spec.Configs["cleanup.policy"].Should().Be("delete");
    }
}
