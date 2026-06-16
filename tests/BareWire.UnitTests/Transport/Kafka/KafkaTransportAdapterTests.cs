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

    private static KafkaTransportOptions ValidOptionsWithGroup() =>
        new() { BootstrapServers = "localhost:9092", GroupId = "test-group" };

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
        // Arrange
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

    // ── DeployTopologyAsync stub (remains until R1.4) ─────────────────────────

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

    // ── ConsumeAsync guard tests ──────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_EmptyEndpoint_ThrowsArgumentException()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptionsWithGroup(), CreateLogger());
        var flowControl = new FlowControlOptions();

        // Act — guards fire on first MoveNextAsync of the async iterator
        Func<Task> act = async () =>
        {
            await foreach (InboundMessage _ in adapter.ConsumeAsync("", flowControl))
            {
            }
        };

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ConsumeAsync_NullEndpoint_ThrowsArgumentException()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptionsWithGroup(), CreateLogger());
        var flowControl = new FlowControlOptions();

        // Act
        Func<Task> act = async () =>
        {
            await foreach (InboundMessage _ in adapter.ConsumeAsync(null!, flowControl))
            {
            }
        };

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ConsumeAsync_MissingGroupId_ThrowsBareWireConfigurationException()
    {
        // Arrange — valid BootstrapServers but no GroupId
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());
        var flowControl = new FlowControlOptions();

        // Act
        Func<Task> act = async () =>
        {
            await foreach (InboundMessage _ in adapter.ConsumeAsync("my-topic", flowControl))
            {
            }
        };

        // Assert — ValidateConsumer fires before building the native consumer
        await act.Should().ThrowAsync<BareWireConfigurationException>()
            .WithMessage("*GroupId*");
    }

    [Fact]
    public async Task ConsumeAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptionsWithGroup(), CreateLogger());
        await adapter.DisposeAsync();
        var flowControl = new FlowControlOptions();

        // Act
        Func<Task> act = async () =>
        {
            await foreach (InboundMessage _ in adapter.ConsumeAsync("my-topic", flowControl))
            {
            }
        };

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // ── SettleAsync guard tests ───────────────────────────────────────────────

    [Fact]
    public async Task SettleAsync_NullMessage_ThrowsArgumentNullException()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());

        // Act
        Func<Task> act = () => adapter.SettleAsync(SettlementAction.Ack, null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SettleAsync_Defer_ThrowsNotSupportedException()
    {
        // Arrange — Defer is checked before consumer resolution
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());
        var message = new InboundMessage(
            messageId: "msg-defer",
            headers: new Dictionary<string, string> { ["BW-ConsumerId"] = "some-id" },
            body: ReadOnlySequence<byte>.Empty,
            deliveryTag: 1UL);

        // Act
        Func<Task> act = () => adapter.SettleAsync(SettlementAction.Defer, message);

        // Assert
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task SettleAsync_MissingBwConsumerIdHeader_ThrowsBareWireTransportException()
    {
        // Arrange — message with no BW-ConsumerId header
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());
        var message = new InboundMessage(
            messageId: "msg-no-id",
            headers: new Dictionary<string, string>(),
            body: ReadOnlySequence<byte>.Empty,
            deliveryTag: 1UL);

        // Act
        Func<Task> act = () => adapter.SettleAsync(SettlementAction.Ack, message);

        // Assert
        await act.Should().ThrowAsync<BareWireTransportException>();
    }

    [Fact]
    public async Task SettleAsync_NoActiveConsumer_ThrowsBareWireTransportException()
    {
        // Arrange — message has BW-ConsumerId but no active consumer with that id
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());
        var message = new InboundMessage(
            messageId: "msg-unknown",
            headers: new Dictionary<string, string> { ["BW-ConsumerId"] = "does-not-exist" },
            body: ReadOnlySequence<byte>.Empty,
            deliveryTag: 1UL);

        // Act
        Func<Task> act = () => adapter.SettleAsync(SettlementAction.Ack, message);

        // Assert
        await act.Should().ThrowAsync<BareWireTransportException>();
    }

    [Fact]
    public async Task SettleAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptions(), CreateLogger());
        var message = new InboundMessage(
            messageId: "msg-disposed",
            headers: new Dictionary<string, string> { ["BW-ConsumerId"] = "some-id" },
            body: ReadOnlySequence<byte>.Empty,
            deliveryTag: 1UL);
        await adapter.DisposeAsync();

        // Act
        Func<Task> act = () => adapter.SettleAsync(SettlementAction.Ack, message);

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>();
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
