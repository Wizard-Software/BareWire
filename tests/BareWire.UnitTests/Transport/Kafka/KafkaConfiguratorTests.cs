using AwesomeAssertions;
using BareWire.Transport.Kafka;
using BareWire.Transport.Kafka.Configuration;
using Confluent.Kafka;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaConfiguratorTests
{
    // Helper: configurator with valid BootstrapServers already set
    private static KafkaConfigurator WithBootstrap(string servers = "localhost:9092")
    {
        var c = new KafkaConfigurator();
        c.BootstrapServers(servers);
        return c;
    }

    // ── BootstrapServers ──────────────────────────────────────────────────────

    [Fact]
    public void Build_WithBootstrapServers_SetsBootstrapServers()
    {
        // Arrange
        KafkaConfigurator configurator = WithBootstrap("broker1:9092,broker2:9092");

        // Act
        KafkaTransportOptions options = configurator.Build();

        // Assert
        options.BootstrapServers.Should().Be("broker1:9092,broker2:9092");
    }

    // ── ConsumerGroup ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_WithConsumerGroup_SetsGroupId()
    {
        // Arrange
        KafkaConfigurator configurator = WithBootstrap();
        configurator.ConsumerGroup("my-service-group");

        // Act
        KafkaTransportOptions options = configurator.Build();

        // Assert
        options.GroupId.Should().Be("my-service-group");
    }

    [Fact]
    public void ConsumerGroup_EmptyGroupId_ThrowsArgumentException()
    {
        // Arrange
        KafkaConfigurator configurator = WithBootstrap();

        // Act
        Action act = () => configurator.ConsumerGroup(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // ── ConsumerAutoOffsetReset ───────────────────────────────────────────────

    [Fact]
    public void Build_WithAutoOffsetResetLatest_SetsAutoOffsetResetLatest()
    {
        // Arrange
        KafkaConfigurator configurator = WithBootstrap();
        configurator.ConsumerAutoOffsetReset(AutoOffsetReset.Latest);

        // Act
        KafkaTransportOptions options = configurator.Build();

        // Assert
        options.AutoOffsetReset.Should().Be(AutoOffsetReset.Latest);
    }

    [Fact]
    public void Build_WithoutAutoOffsetReset_DefaultsToEarliest()
    {
        // Arrange — no ConsumerAutoOffsetReset call
        KafkaConfigurator configurator = WithBootstrap();

        // Act
        KafkaTransportOptions options = configurator.Build();

        // Assert
        options.AutoOffsetReset.Should().Be(AutoOffsetReset.Earliest);
    }

    // ── ConsumerPartitionAssignmentStrategy ───────────────────────────────────

    [Fact]
    public void Build_WithRoundRobinStrategy_SetsRoundRobin()
    {
        // Arrange
        KafkaConfigurator configurator = WithBootstrap();
        configurator.ConsumerPartitionAssignmentStrategy(KafkaPartitionAssignmentStrategy.RoundRobin);

        // Act
        KafkaTransportOptions options = configurator.Build();

        // Assert
        options.ConsumerPartitionAssignmentStrategy.Should().Be(KafkaPartitionAssignmentStrategy.RoundRobin);
    }

    [Fact]
    public void Build_WithoutStrategy_DefaultsToCooperativeSticky()
    {
        // Arrange — no ConsumerPartitionAssignmentStrategy call
        KafkaConfigurator configurator = WithBootstrap();

        // Act
        KafkaTransportOptions options = configurator.Build();

        // Assert — D9 default
        options.ConsumerPartitionAssignmentStrategy.Should().Be(KafkaPartitionAssignmentStrategy.CooperativeSticky);
    }

    // ── Build validation ──────────────────────────────────────────────────────

    [Fact]
    public void Build_WithoutBootstrapServers_ThrowsBareWireConfigurationException()
    {
        // Arrange — no BootstrapServers call
        var configurator = new KafkaConfigurator();

        // Act
        Action act = () => configurator.Build();

        // Assert — Validate() is called from Build()
        act.Should().Throw<BareWire.Abstractions.Exceptions.BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaTransportOptions.BootstrapServers));
    }

    // ── Spec-required exact test names (C4) ──────────────────────────────────

    /// <summary>
    /// Required by spec C4: bootstrap + consumer group flows through to GroupId.
    /// </summary>
    [Fact]
    public void Build_WithBootstrapAndConsumerGroup_SetsGroupId()
    {
        // Arrange
        var configurator = new KafkaConfigurator();
        configurator.BootstrapServers("localhost:9092");
        configurator.ConsumerGroup("order-service-group");

        // Act
        KafkaTransportOptions options = configurator.Build();

        // Assert
        options.GroupId.Should().Be("order-service-group");
    }

    /// <summary>
    /// Required by spec C4: ConsumerAutoOffsetReset flows through to AutoOffsetReset.
    /// </summary>
    [Fact]
    public void Build_WithConsumerAutoOffsetReset_SetsOption()
    {
        // Arrange
        KafkaConfigurator configurator = WithBootstrap();
        configurator.ConsumerAutoOffsetReset(AutoOffsetReset.Latest);

        // Act
        KafkaTransportOptions options = configurator.Build();

        // Assert
        options.AutoOffsetReset.Should().Be(AutoOffsetReset.Latest);
    }

    /// <summary>
    /// Required by spec C4: ConsumerPartitionAssignmentStrategy flows through.
    /// </summary>
    [Fact]
    public void Build_WithConsumerPartitionAssignmentStrategy_SetsOption()
    {
        // Arrange
        KafkaConfigurator configurator = WithBootstrap();
        configurator.ConsumerPartitionAssignmentStrategy(KafkaPartitionAssignmentStrategy.Range);

        // Act
        KafkaTransportOptions options = configurator.Build();

        // Assert
        options.ConsumerPartitionAssignmentStrategy.Should().Be(KafkaPartitionAssignmentStrategy.Range);
    }

    /// <summary>
    /// Required by spec C4: when no consumer fluent calls are made, default values are kept.
    /// </summary>
    [Fact]
    public void Build_WithoutConsumerCalls_KeepsDefaults()
    {
        // Arrange — only BootstrapServers, no consumer calls
        KafkaConfigurator configurator = WithBootstrap();

        // Act
        KafkaTransportOptions options = configurator.Build();

        // Assert — D9 defaults
        options.GroupId.Should().Be(string.Empty);
        options.ConsumerPartitionAssignmentStrategy.Should().Be(KafkaPartitionAssignmentStrategy.CooperativeSticky);
        options.AutoOffsetReset.Should().Be(AutoOffsetReset.Earliest);
    }

    /// <summary>
    /// Required by spec C4: ConsumerGroup throws on empty or null group id.
    /// </summary>
    [Fact]
    public void ConsumerGroup_EmptyOrNull_ThrowsArgumentException()
    {
        // Arrange
        KafkaConfigurator configurator = WithBootstrap();

        // Act + Assert — empty
        Action actEmpty = () => configurator.ConsumerGroup(string.Empty);
        actEmpty.Should().Throw<ArgumentException>();

        // Act + Assert — null
        Action actNull = () => configurator.ConsumerGroup(null!);
        actNull.Should().Throw<ArgumentException>();
    }
}
