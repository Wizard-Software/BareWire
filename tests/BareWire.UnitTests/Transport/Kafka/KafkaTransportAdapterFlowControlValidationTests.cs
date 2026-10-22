using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaTransportAdapterFlowControlValidationTests
{
    private static KafkaTransportOptions ValidOptionsWithGroup() =>
        new() { BootstrapServers = "localhost:9092", GroupId = "test-group" };

    private static async Task DrainAsync(
        KafkaTransportAdapter adapter, FlowControlOptions flowControl, CancellationToken cancellationToken)
    {
        await foreach (InboundMessage _ in adapter.ConsumeAsync("orders", flowControl, cancellationToken))
        {
        }
    }

    // The Drop* cases reproduce the defect: they are accepted, a native consumer is built, and the iterator
    // waits until the test token is cancelled. The InternalQueueCapacity cases already throw from
    // BoundedChannelOptions, so they are regression guards rather than reproductions.
    [Theory]
    [InlineData(BoundedChannelFullMode.DropWrite, 1000)]
    [InlineData(BoundedChannelFullMode.DropOldest, 1000)]
    [InlineData(BoundedChannelFullMode.DropNewest, 1000)]
    [InlineData(BoundedChannelFullMode.Wait, 0)]
    [InlineData(BoundedChannelFullMode.Wait, -1)]
    public async Task ConsumeAsync_InvalidFlowControl_ThrowsArgumentOutOfRangeException(
        BoundedChannelFullMode fullMode, int internalQueueCapacity)
    {
        // Arrange
        var adapter = new KafkaTransportAdapter(ValidOptionsWithGroup(), NullLogger<KafkaTransportAdapter>.Instance);
        await using var _ = adapter;
        var flowControl = new FlowControlOptions
        {
            FullMode = fullMode,
            InternalQueueCapacity = internalQueueCapacity,
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        // Act
        Func<Task> act = () => DrainAsync(adapter, flowControl, cts.Token);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ConsumeAsync_InvalidFullModeWithoutGroupId_ThrowsArgumentOutOfRangeExceptionFirst()
    {
        // Arrange — validation runs before the consumer options check: a missing GroupId would otherwise
        // surface as BareWireConfigurationException, so this proves the flow-control check comes first.
        var adapter = new KafkaTransportAdapter(
            new KafkaTransportOptions { BootstrapServers = "localhost:9092" },
            NullLogger<KafkaTransportAdapter>.Instance);
        await using var _ = adapter;
        var flowControl = new FlowControlOptions { FullMode = BoundedChannelFullMode.DropWrite };

        // Act
        Func<Task> act = () => DrainAsync(adapter, flowControl, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
