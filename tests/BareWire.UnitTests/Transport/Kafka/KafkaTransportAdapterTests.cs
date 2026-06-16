using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaTransportAdapterTests
{
    private static NullLogger<KafkaTransportAdapter> CreateLogger() =>
        NullLogger<KafkaTransportAdapter>.Instance;

    private static KafkaTransportOptions ValidOptions() =>
        new() { BootstrapServers = "localhost:9092" };

    // ── TransportName ─────────────────────────────────────────────────────────

    [Fact]
    public void TransportName_ReturnsKafka()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());

        // Act
        string name = adapter.TransportName;

        // Assert
        name.Should().Be("Kafka");
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_HasExactlyOnce()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());

        // Act
        TransportCapabilities caps = adapter.Capabilities;

        // Assert — ExactlyOnce: idempotent producer enables exactly-once per-producer semantics
        caps.Should().HaveFlag(TransportCapabilities.ExactlyOnce);
    }

    [Fact]
    public void Capabilities_HasOrderingKeys()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());

        // Act
        TransportCapabilities caps = adapter.Capabilities;

        // Assert — OrderingKeys: BW-PartitionKey header enables per-key ordering
        caps.Should().HaveFlag(TransportCapabilities.OrderingKeys);
    }

    [Fact]
    public void Capabilities_HasBatchReceive()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());

        // Act
        TransportCapabilities caps = adapter.Capabilities;

        // Assert — BatchReceive: Kafka consumer natively supports batch fetching
        caps.Should().HaveFlag(TransportCapabilities.BatchReceive);
    }

    [Fact]
    public void Capabilities_DoesNotHaveTransactions()
    {
        // Arrange — GAP-4: Transactions capability NOT declared in R1.1 (no transactional producer)
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());

        // Act
        TransportCapabilities caps = adapter.Capabilities;

        // Assert
        caps.Should().NotHaveFlag(TransportCapabilities.Transactions);
    }

    [Fact]
    public void Capabilities_DoesNotHaveDlqNative()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());

        // Act
        TransportCapabilities caps = adapter.Capabilities;

        // Assert — DLQ native support is not part of R1.1
        caps.Should().NotHaveFlag(TransportCapabilities.DlqNative);
    }

    // ── Constructor guards ────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        // Arrange
        var logger = CreateLogger();

        // Act
        Action act = () => _ = new KafkaTransportAdapter(null!, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("options");
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Arrange
        KafkaTransportOptions options = ValidOptions();

        // Act
        Action act = () => _ = new KafkaTransportAdapter(options, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    public void Constructor_InvalidOptions_ThrowsBareWireConfigurationException()
    {
        // Arrange — options with empty BootstrapServers should fail validation in the ctor
        var options = new KafkaTransportOptions { BootstrapServers = string.Empty };
        var logger = CreateLogger();

        // Act
        Action act = () => _ = new KafkaTransportAdapter(options, logger);

        // Assert
        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(KafkaTransportOptions.BootstrapServers));
    }

    // ── Stubs (NotSupportedException) ─────────────────────────────────────────

    [Fact]
    public void ConsumeAsync_ThrowsNotSupportedException()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());
        var flowControl = new FlowControlOptions();

        // Act
        Action act = () => _ = adapter.ConsumeAsync("test-topic", flowControl);

        // Assert
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*R1.2*");
    }

    [Fact]
    public async Task SettleAsync_ThrowsNotSupportedException()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());
        var message = new InboundMessage(
            messageId: "msg-1",
            headers: new Dictionary<string, string>(),
            body: ReadOnlySequence<byte>.Empty,
            deliveryTag: 1UL);

        // Act
        Func<Task> act = () => adapter.SettleAsync(SettlementAction.Ack, message);

        // Assert
        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*R1.2*");
    }

    [Fact]
    public async Task DeployTopologyAsync_ThrowsNotSupportedException()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());
        var topology = new TopologyDeclaration();

        // Act
        Func<Task> act = () => adapter.DeployTopologyAsync(topology);

        // Assert
        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*R1.4*");
    }

    // ── DI registration ───────────────────────────────────────────────────────

    [Fact]
    public async Task AddBareWireKafka_RegistersITransportAdapterAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddBareWireKafka(kafka => kafka.BootstrapServers("localhost:9092"));

        // KafkaTransportAdapter implements only IAsyncDisposable — use await using
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Assert — resolves without throwing
        var adapter1 = provider.GetService<ITransportAdapter>();
        var adapter2 = provider.GetService<ITransportAdapter>();

        adapter1.Should().NotBeNull();
        adapter1.Should().BeSameAs(adapter2, "ITransportAdapter must be Singleton");
        adapter1!.TransportName.Should().Be("Kafka");
    }
}
