using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.AzureServiceBus.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

public sealed class AzureServiceBusSettlementRouterTests
{
    [Fact]
    public void Map_Ack_ReturnsComplete()
    {
        AzureServiceBusSettlementRouter.Map(SettlementAction.Ack)
            .Should().Be(AzureServiceBusSettlementOperation.Complete);
    }

    [Fact]
    public void Map_Nack_ReturnsAbandon()
    {
        AzureServiceBusSettlementRouter.Map(SettlementAction.Nack)
            .Should().Be(AzureServiceBusSettlementOperation.Abandon);
    }

    [Fact]
    public void Map_Requeue_ReturnsAbandon()
    {
        // Requeue and Nack both map to Abandon — release lock, broker redelivers.
        AzureServiceBusSettlementRouter.Map(SettlementAction.Requeue)
            .Should().Be(AzureServiceBusSettlementOperation.Abandon);
    }

    [Fact]
    public void Map_Reject_ReturnsDeadLetter()
    {
        AzureServiceBusSettlementRouter.Map(SettlementAction.Reject)
            .Should().Be(AzureServiceBusSettlementOperation.DeadLetter);
    }

    [Fact]
    public void Map_Defer_ReturnsDefer()
    {
        AzureServiceBusSettlementRouter.Map(SettlementAction.Defer)
            .Should().Be(AzureServiceBusSettlementOperation.Defer);
    }

    [Fact]
    public void Map_UnknownAction_ThrowsArgumentOutOfRangeException()
    {
        // Arrange — a value outside the defined enum range.
        var bogus = (SettlementAction)999;

        // Act
        Action act = () => AzureServiceBusSettlementRouter.Map(bogus);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("action");
    }

    [Fact]
    public void Map_NackAndRequeue_MappedToSameOperation()
    {
        // Verify that both no-store actions share the same ASB operation.
        AzureServiceBusSettlementOperation nackOp = AzureServiceBusSettlementRouter.Map(SettlementAction.Nack);
        AzureServiceBusSettlementOperation requeueOp = AzureServiceBusSettlementRouter.Map(SettlementAction.Requeue);

        nackOp.Should().Be(requeueOp);
    }
}
