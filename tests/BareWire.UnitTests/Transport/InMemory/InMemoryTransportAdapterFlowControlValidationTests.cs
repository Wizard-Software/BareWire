using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryTransportAdapterFlowControlValidationTests
{
    private static InMemoryTransportAdapter CreateAdapter()
    {
        var c = new InMemoryConfigurator();
        c.ConfigureTopology(t => t.DeclareQueue("orders"));
        InMemoryTransportOptions o = c.Build();
        return new InMemoryTransportAdapter(o, new InMemoryBroker(o)) { SettlementBudget = TimeSpan.Zero };
    }

    [Theory]
    [InlineData(BoundedChannelFullMode.DropWrite, 1000)]
    [InlineData(BoundedChannelFullMode.DropOldest, 1000)]
    [InlineData(BoundedChannelFullMode.DropNewest, 1000)]
    [InlineData(BoundedChannelFullMode.Wait, 0)]
    [InlineData(BoundedChannelFullMode.Wait, -1)]
    public void ConsumeAsync_InvalidFlowControl_ThrowsArgumentOutOfRangeException(
        BoundedChannelFullMode fullMode, int internalQueueCapacity)
    {
        // Arrange
        InMemoryTransportAdapter adapter = CreateAdapter();
        var flowControl = new FlowControlOptions
        {
            FullMode = fullMode,
            InternalQueueCapacity = internalQueueCapacity,
        };

        // Act — ConsumeAsync is not an iterator, so the guards throw synchronously.
        Action act = () => adapter.ConsumeAsync("orders", flowControl, TestContext.Current.CancellationToken);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ConsumeAsync_InvalidFullModeOnUndeclaredQueue_ThrowsArgumentOutOfRangeExceptionFirst()
    {
        // Arrange — validation runs before the registry check, which would otherwise reject the unknown queue.
        InMemoryTransportAdapter adapter = CreateAdapter();
        var flowControl = new FlowControlOptions { FullMode = BoundedChannelFullMode.DropWrite };

        // Act
        Action act = () => adapter.ConsumeAsync("no-such-queue", flowControl, TestContext.Current.CancellationToken);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ConsumeAsync_WaitWithPositiveCapacity_ReturnsStream()
    {
        // Arrange — the smallest valid options must still pass validation.
        InMemoryTransportAdapter adapter = CreateAdapter();
        var flowControl = new FlowControlOptions
        {
            FullMode = BoundedChannelFullMode.Wait,
            InternalQueueCapacity = 1,
        };
        IAsyncEnumerable<InboundMessage>? stream = null;

        // Act
        Action act = () => stream = adapter.ConsumeAsync("orders", flowControl, TestContext.Current.CancellationToken);

        // Assert
        act.Should().NotThrow();
        stream.Should().NotBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-998)]
    public void ConsumeAsync_NonPositiveMaxInFlightMessages_ThrowsArgumentOutOfRangeException(int maxInFlightMessages)
    {
        // Arrange
        InMemoryTransportAdapter adapter = CreateAdapter();
        var flowControl = new FlowControlOptions { MaxInFlightMessages = maxInFlightMessages };

        // Act — ConsumeAsync is not an iterator, so the guards throw synchronously.
        Action act = () => adapter.ConsumeAsync("orders", flowControl, TestContext.Current.CancellationToken);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("flowControl.MaxInFlightMessages");
    }

    [Fact]
    public void ConsumeAsync_ZeroMaxInFlightMessagesOnUndeclaredQueue_ThrowsArgumentOutOfRangeExceptionFirst()
    {
        // Arrange — validation runs before the registry check, which would otherwise reject the unknown queue.
        InMemoryTransportAdapter adapter = CreateAdapter();
        var flowControl = new FlowControlOptions { MaxInFlightMessages = 0 };

        // Act
        Action act = () => adapter.ConsumeAsync("no-such-queue", flowControl, TestContext.Current.CancellationToken);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ConsumeAsync_MaxInFlightMessagesOne_ReturnsStream()
    {
        // Arrange — the smallest valid value must still pass validation.
        InMemoryTransportAdapter adapter = CreateAdapter();
        var flowControl = new FlowControlOptions { MaxInFlightMessages = 1 };
        IAsyncEnumerable<InboundMessage>? stream = null;

        // Act
        Action act = () => stream = adapter.ConsumeAsync("orders", flowControl, TestContext.Current.CancellationToken);

        // Assert
        act.Should().NotThrow();
        stream.Should().NotBeNull();
    }
}
