using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.AWS.SQS.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.Sqs;

public sealed class SqsSettlementRouterTests
{
    [Fact]
    public void Map_Ack_ReturnsDelete()
    {
        SqsSettlementRouter.Map(SettlementAction.Ack)
            .Should().Be(SqsSettlementOperation.Delete);
    }

    [Fact]
    public void Map_Nack_ReturnsChangeVisibility()
    {
        SqsSettlementRouter.Map(SettlementAction.Nack)
            .Should().Be(SqsSettlementOperation.ChangeVisibility);
    }

    [Fact]
    public void Map_Requeue_ReturnsChangeVisibility()
    {
        SqsSettlementRouter.Map(SettlementAction.Requeue)
            .Should().Be(SqsSettlementOperation.ChangeVisibility);
    }

    [Fact]
    public void Map_Defer_ReturnsChangeVisibility()
    {
        SqsSettlementRouter.Map(SettlementAction.Defer)
            .Should().Be(SqsSettlementOperation.ChangeVisibility);
    }

    [Fact]
    public void Map_Reject_ReturnsDeadLetterViaRedrive()
    {
        // ADR-014 / GAP-3: Reject must NOT map to Delete — it leaves message for RedrivePolicy.
        SqsSettlementRouter.Map(SettlementAction.Reject)
            .Should().Be(SqsSettlementOperation.DeadLetterViaRedrive);
    }

    [Fact]
    public void Map_NackAndRequeue_MappedToSameOperation()
    {
        SqsSettlementOperation nackOp = SqsSettlementRouter.Map(SettlementAction.Nack);
        SqsSettlementOperation requeueOp = SqsSettlementRouter.Map(SettlementAction.Requeue);

        nackOp.Should().Be(requeueOp);
    }

    [Fact]
    public void Map_Reject_DoesNotMapToDelete()
    {
        // Explicit safety check: Reject must never result in a Delete operation (ADR-014 / GAP-3).
        SqsSettlementRouter.Map(SettlementAction.Reject)
            .Should().NotBe(SqsSettlementOperation.Delete,
                "deleting on Reject would silently discard the message without triggering the DLQ (ADR-014)");
    }

    [Fact]
    public void Map_UnknownAction_ThrowsArgumentOutOfRangeException()
    {
        var bogus = (SettlementAction)999;

        Action act = () => SqsSettlementRouter.Map(bogus);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("action");
    }
}
