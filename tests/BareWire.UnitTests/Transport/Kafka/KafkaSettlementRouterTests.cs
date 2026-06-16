using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.Kafka.Internal;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaSettlementRouterTests
{
    private const int MaxRetryCount = 3;

    // ── Ack / Requeue (retry-count-independent) ────────────────────────────────

    [Fact]
    public void Decide_Ack_ReturnsStoreOffset()
    {
        KafkaSettlementRouter.Decide(SettlementAction.Ack, currentRetryCount: 0, MaxRetryCount)
            .Should().Be(SettlementOutcome.StoreOffset);
    }

    [Fact]
    public void Decide_Requeue_ReturnsNoStore()
    {
        KafkaSettlementRouter.Decide(SettlementAction.Requeue, currentRetryCount: 0, MaxRetryCount)
            .Should().Be(SettlementOutcome.NoStore);
    }

    // ── Defer ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Decide_Defer_BelowCap_ReturnsRepublishRetryThenStore(int retryCount)
    {
        KafkaSettlementRouter.Decide(SettlementAction.Defer, retryCount, MaxRetryCount)
            .Should().Be(SettlementOutcome.RepublishRetryThenStore);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Decide_Defer_AtOrAboveCap_ReturnsRepublishDlqThenStore(int retryCount)
    {
        KafkaSettlementRouter.Decide(SettlementAction.Defer, retryCount, MaxRetryCount)
            .Should().Be(SettlementOutcome.RepublishDlqThenStore);
    }

    // ── Reject (always DLQ) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Decide_Reject_AlwaysReturnsRepublishDlqThenStore(int retryCount)
    {
        KafkaSettlementRouter.Decide(SettlementAction.Reject, retryCount, MaxRetryCount)
            .Should().Be(SettlementOutcome.RepublishDlqThenStore);
    }

    // ── Nack (no-store below cap, DLQ at cap — poison guard) ────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Decide_Nack_BelowCap_ReturnsNoStore(int retryCount)
    {
        KafkaSettlementRouter.Decide(SettlementAction.Nack, retryCount, MaxRetryCount)
            .Should().Be(SettlementOutcome.NoStore);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void Decide_Nack_AtOrAboveCap_ReturnsRepublishDlqThenStore(int retryCount)
    {
        KafkaSettlementRouter.Decide(SettlementAction.Nack, retryCount, MaxRetryCount)
            .Should().Be(SettlementOutcome.RepublishDlqThenStore);
    }

    [Fact]
    public void Decide_UnknownAction_ThrowsArgumentOutOfRangeException()
    {
        // Arrange — a value outside the defined enum range
        var bogus = (SettlementAction)999;

        // Act
        Action act = () => KafkaSettlementRouter.Decide(bogus, 0, MaxRetryCount);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("action");
    }

    // ── SEC-1: ClampRetryCount neutralises spoofed wire values ───────────────────

    [Fact]
    public void ClampRetryCount_SpoofedHighValue_ClampsToMax()
    {
        // SEC-1: a spoofed BW-RetryCount=999 must not force a premature DLQ beyond the cap.
        KafkaSettlementRouter.ClampRetryCount(wireRetryCount: 999, maxRetryCount: MaxRetryCount)
            .Should().Be(MaxRetryCount);
    }

    [Fact]
    public void ClampRetryCount_NegativeValue_ClampsToZero()
    {
        // SEC-1: a negative value must not be usable to cause an unbounded retry loop.
        KafkaSettlementRouter.ClampRetryCount(wireRetryCount: -100, maxRetryCount: MaxRetryCount)
            .Should().Be(0);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void ClampRetryCount_WithinRange_PassesThrough(int wire, int expected)
    {
        KafkaSettlementRouter.ClampRetryCount(wire, MaxRetryCount).Should().Be(expected);
    }

    [Fact]
    public void Decide_SpoofedRetryCountClampedFirst_DefersToDlqAtCap()
    {
        // SEC-1 end-to-end: clamp(999) == 3 == cap → Defer routes to DLQ (not an out-of-range branch).
        int clamped = KafkaSettlementRouter.ClampRetryCount(999, MaxRetryCount);

        KafkaSettlementRouter.Decide(SettlementAction.Defer, clamped, MaxRetryCount)
            .Should().Be(SettlementOutcome.RepublishDlqThenStore);
    }
}
