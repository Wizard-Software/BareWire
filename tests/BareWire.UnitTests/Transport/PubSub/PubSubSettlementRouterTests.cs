using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.Google.PubSub.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.PubSub;

public sealed class PubSubSettlementRouterTests
{
    [Fact]
    public void Map_Ack_ReturnsAcknowledge()
    {
        PubSubSettlementOperation result = PubSubSettlementRouter.Map(SettlementAction.Ack);

        result.Should().Be(PubSubSettlementOperation.Acknowledge);
    }

    [Fact]
    public void Map_Nack_ReturnsModifyAckDeadlineZero()
    {
        PubSubSettlementOperation result = PubSubSettlementRouter.Map(SettlementAction.Nack);

        result.Should().Be(PubSubSettlementOperation.ModifyAckDeadlineZero);
    }

    [Fact]
    public void Map_Requeue_ReturnsModifyAckDeadlineZero()
    {
        PubSubSettlementOperation result = PubSubSettlementRouter.Map(SettlementAction.Requeue);

        result.Should().Be(PubSubSettlementOperation.ModifyAckDeadlineZero);
    }

    [Fact]
    public void Map_Defer_ReturnsModifyAckDeadlineZero()
    {
        PubSubSettlementOperation result = PubSubSettlementRouter.Map(SettlementAction.Defer);

        result.Should().Be(PubSubSettlementOperation.ModifyAckDeadlineZero);
    }

    [Fact]
    public void Map_Reject_ReturnsDeadLetterViaPolicy()
    {
        PubSubSettlementOperation result = PubSubSettlementRouter.Map(SettlementAction.Reject);

        result.Should().Be(PubSubSettlementOperation.DeadLetterViaPolicy);
    }

    [Fact]
    public void Map_UnknownAction_ThrowsArgumentOutOfRangeException()
    {
        const SettlementAction unknownAction = (SettlementAction)999;

        Action act = () => PubSubSettlementRouter.Map(unknownAction);

        act.Should().ThrowExactly<ArgumentOutOfRangeException>()
            .WithParameterName("action");
    }
}
