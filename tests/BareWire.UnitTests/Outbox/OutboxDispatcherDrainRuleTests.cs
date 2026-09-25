using AwesomeAssertions;
using BareWire.Outbox;

namespace BareWire.UnitTests.Outbox;

// Pure drain-rule decision of the outbox polling loop: drain again immediately only when the batch
// came back full, at least half of it was confirmed, and the drain streak is under its cap — which is
// halved once the current streak has seen a transport nack.
public sealed class OutboxDispatcherDrainRuleTests
{
    [Theory]
    [InlineData(4, 4, 4, 0, false, true)]   // full batch, all confirmed
    [InlineData(4, 3, 4, 0, true, true)]    // full batch, minority nacked (25%)
    [InlineData(2, 1, 2, 0, true, true)]    // full batch, exactly half confirmed
    [InlineData(3, 1, 3, 0, true, false)]   // full batch, majority nacked
    [InlineData(4, 0, 4, 0, true, false)]   // full batch, all nacked
    [InlineData(3, 3, 4, 0, false, false)]  // partial (not full) batch
    [InlineData(0, 0, 4, 0, false, false)]  // empty batch or transient error
    public void ShouldDrainImmediately_ForBatchOutcome_ReturnsExpected(
        int claimed,
        int confirmed,
        int batchSize,
        int consecutiveDrains,
        bool streakHasNacks,
        bool expected)
    {
        bool result = OutboxDispatcher.ShouldDrainImmediately(
            claimed, confirmed, batchSize, consecutiveDrains, streakHasNacks);

        result.Should().Be(expected);
    }

    [Fact]
    public void ShouldDrainImmediately_StreakWithNacksAtHalfCap_ReturnsFalse()
    {
        int halfCap = OutboxDispatcher.MaxConsecutiveDrainsWithNacks;

        OutboxDispatcher.ShouldDrainImmediately(2, 1, 2, halfCap - 1, streakHasNacks: true)
            .Should().BeTrue("the streak is still one drain below the halved cap");
        OutboxDispatcher.ShouldDrainImmediately(2, 1, 2, halfCap, streakHasNacks: true)
            .Should().BeFalse("a streak that has seen a nack stops at the halved cap");
    }

    [Fact]
    public void ShouldDrainImmediately_StreakWithoutNacks_UsesFullCap()
    {
        OutboxDispatcher.ShouldDrainImmediately(
                2, 2, 2, OutboxDispatcher.MaxConsecutiveDrainsWithNacks, streakHasNacks: false)
            .Should().BeTrue("a clean streak is not limited by the halved cap");
        OutboxDispatcher.ShouldDrainImmediately(
                2, 2, 2, OutboxDispatcher.MaxConsecutiveDrains, streakHasNacks: false)
            .Should().BeFalse("a clean streak stops at the full cap");
    }

    [Fact]
    public void ShouldDrainImmediately_LargeCounts_DoesNotOverflow()
    {
        OutboxDispatcher.ShouldDrainImmediately(
                int.MaxValue, int.MaxValue / 2 + 1, 10_000, 0, streakHasNacks: true)
            .Should().BeTrue("2 x Confirmed is computed without overflow");
    }

    [Fact]
    public void MaxConsecutiveDrainsWithNacks_IsHalfOfFullCap()
    {
        OutboxDispatcher.MaxConsecutiveDrainsWithNacks.Should().Be(OutboxDispatcher.MaxConsecutiveDrains / 2);
        OutboxDispatcher.MaxConsecutiveDrainsWithNacks.Should().BeGreaterThan(0);
    }
}
