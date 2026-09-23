using AwesomeAssertions;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework.Internal;
using Xunit;

namespace BareWire.UnitTests.Outbox;

/// <summary>
/// Pure arithmetic tests for <see cref="OutboxFairClaimPlan"/> — the batch-splitting rules that
/// decide how many "new" rows and how many "due retry" rows a claim cycle should take.
/// </summary>
public sealed class OutboxFairClaimPlanTests
{
    // ── GetEffectiveBatchSize ─────────────────────────────────────────────────

    [Theory]
    [InlineData(10, 0, 10)]
    [InlineData(10, 4, 6)]
    [InlineData(10, 10, 0)]
    [InlineData(10, 25, 0)]
    [InlineData(10, -1, 10)]
    [InlineData(0, 0, 0)]
    public void GetEffectiveBatchSize_BatchSizeAndCarryForward_ReturnsRemainingCapacity(
        int batchSize,
        int carryForwardCount,
        int expected)
    {
        int actual = OutboxFairClaimPlan.GetEffectiveBatchSize(batchSize, carryForwardCount);

        actual.Should().Be(expected);
    }

    // ── GetRetryReserve ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(7, 1)]
    [InlineData(8, 2)]
    [InlineData(100, 25)]
    public void GetRetryReserve_EffectiveBatchSizeTwoOrMore_ReturnsMaxOfOneAndQuarter(
        int effectiveBatchSize,
        int expectedReserve)
    {
        int actual = OutboxFairClaimPlan.GetRetryReserve(effectiveBatchSize, singleSlotRetryTurn: false);

        actual.Should().Be(expectedReserve);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void GetRetryReserve_SingleSlot_ReturnsReserveBasedOnTurn(bool singleSlotRetryTurn, int expectedReserve)
    {
        int actual = OutboxFairClaimPlan.GetRetryReserve(1, singleSlotRetryTurn);

        actual.Should().Be(expectedReserve);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void GetRetryReserve_NonPositiveEffectiveBatchSize_ReturnsZero(int effectiveBatchSize)
    {
        int actual = OutboxFairClaimPlan.GetRetryReserve(effectiveBatchSize, singleSlotRetryTurn: true);

        actual.Should().Be(0);
    }

    // ── DrawSingleSlotRetryTurn ───────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.4999, true)]
    [InlineData(0.5, false)]
    [InlineData(0.99, false)]
    public void DrawSingleSlotRetryTurn_JitterValue_ReturnsTurnDecision(double jitterValue, bool expected)
    {
        bool actual = OutboxFairClaimPlan.DrawSingleSlotRetryTurn(new FakeJitterSource(jitterValue));

        actual.Should().Be(expected);
    }

    // ── ClampClaimed ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1, 75, 75)]
    [InlineData(0, 75, 0)]
    [InlineData(40, 75, 40)]
    [InlineData(90, 75, 75)]
    [InlineData(5, 0, 0)]
    public void ClampClaimed_ReportedCountAndLimit_ReturnsClampedCount(int reportedCount, int limit, int expected)
    {
        int actual = OutboxFairClaimPlan.ClampClaimed(reportedCount, limit);

        actual.Should().Be(expected);
    }

    // ── ShouldTopUp ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100, 25, 75, 10, true)]
    [InlineData(100, 25, 75, 25, false)]
    [InlineData(100, 25, 40, 10, false)]
    [InlineData(1, 1, 0, 0, true)]
    [InlineData(1, 0, 0, 1, false)]
    [InlineData(1, 0, 1, 0, false)]
    public void ShouldTopUp_ClaimCounts_ReturnsTopUpDecision(
        int effectiveBatchSize,
        int retryReserve,
        int newClaimed,
        int retryClaimed,
        bool expected)
    {
        bool actual = OutboxFairClaimPlan.ShouldTopUp(effectiveBatchSize, retryReserve, newClaimed, retryClaimed);

        actual.Should().Be(expected);
    }

    // ── SplitCandidates ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(4, 1, 10, 10, 3, 1)]
    [InlineData(4, 1, 10, 0, 4, 0)]
    [InlineData(4, 1, 1, 10, 1, 3)]
    [InlineData(100, 25, 200, 200, 75, 25)]
    [InlineData(100, 25, 200, 5, 95, 5)]
    [InlineData(1, 0, 1, 1, 1, 0)]
    [InlineData(1, 1, 1, 1, 0, 1)]
    [InlineData(1, 1, 1, 0, 1, 0)]
    [InlineData(3, 1, 5, 5, 2, 1)]
    [InlineData(2, 1, 5, 0, 2, 0)]
    public void SplitCandidates_ClassCounts_ReturnsTakePerClass(
        int effectiveBatchSize,
        int retryReserve,
        int newCount,
        int retryCount,
        int expectedNewTake,
        int expectedRetryTake)
    {
        (int newTake, int retryTake) = OutboxFairClaimPlan.SplitCandidates(
            effectiveBatchSize,
            retryReserve,
            newCount,
            retryCount);

        newTake.Should().Be(expectedNewTake);
        retryTake.Should().Be(expectedRetryTake);
    }

    private sealed class FakeJitterSource(double value) : IOutboxJitterSource
    {
        public double NextDouble() => value;
    }
}
