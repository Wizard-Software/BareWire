using System.Collections.Concurrent;
using AwesomeAssertions;
using BareWire.Transport.Kafka.Internal;

namespace BareWire.UnitTests.Transport.Kafka;

public sealed class KafkaPartitionCommitTrackerTests
{
    private static KafkaPartitionCommitTracker Create(int max, List<long> stored) =>
        new("t", max, tpo => stored.Add(tpo.Offset.Value));

    private static void TrackRange(KafkaPartitionCommitTracker tracker, int partition, long from, long toInclusive)
    {
        for (long o = from; o <= toInclusive; o++)
        {
            tracker.Track(partition, o);
        }
    }

    [Fact]
    public void Complete_InOrder_StoresNextOffsetEachTime()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);
        TrackRange(tracker, 0, 0, 2);

        tracker.Complete(0, 0).Should().Be(1);
        tracker.Complete(0, 1).Should().Be(2);
        tracker.Complete(0, 2).Should().Be(3);

        stored.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Complete_OutOfOrder_ReturnsNullUntilPrefixIsContiguous()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);
        TrackRange(tracker, 0, 0, 2);

        tracker.Complete(0, 2).Should().BeNull();
        tracker.Complete(0, 1).Should().BeNull();
        tracker.Complete(0, 0).Should().Be(3);

        stored.Should().Equal(3);
    }

    [Fact]
    public void Complete_AfterHoldOfLowerOffset_NeverStoresPositionPastHeldOffset()
    {
        // Arrange — offsets 0 and 1 delivered from partition 0
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);
        tracker.Track(0, 0);
        tracker.Track(0, 1);

        // Act — offset 0 is requeued, then offset 1 is acked
        tracker.Hold(0, 0);
        long? position = tracker.Complete(0, 1);

        // Assert — storing 2 would lose offset 0
        position.Should().BeNull();
        stored.Should().BeEmpty();
        tracker.TrackedCount(0).Should().Be(0);
    }

    [Fact]
    public void Complete_BelowHeldOffset_AdvancesUpToHeldOffsetOnly()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);
        TrackRange(tracker, 0, 0, 2);

        tracker.Hold(0, 1);

        tracker.Complete(0, 2).Should().BeNull();
        tracker.Complete(0, 0).Should().Be(1);
    }

    [Fact]
    public void Track_AboveHeldOffset_IsIgnored()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);
        tracker.Track(0, 0);
        tracker.Hold(0, 0);

        tracker.Track(0, 1).Should().BeFalse();
        tracker.Track(0, 2).Should().BeFalse();

        tracker.TrackedCount(0).Should().Be(0);
        tracker.Complete(0, 1).Should().BeNull();
    }

    [Fact]
    public void Hold_LowerThanExistingHold_MovesHoldDown()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);
        TrackRange(tracker, 0, 0, 3);

        tracker.Hold(0, 2);
        tracker.Hold(0, 1);

        tracker.Complete(0, 0).Should().Be(1);
    }

    [Fact]
    public void Hold_AtOrAboveExistingHold_ReturnsFalseAndKeepsHold()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);
        TrackRange(tracker, 0, 0, 3);

        tracker.Hold(0, 1).Should().BeTrue();
        tracker.Hold(0, 2).Should().BeFalse();
        tracker.Hold(0, 1).Should().BeFalse();

        tracker.Complete(0, 0).Should().Be(1);
    }

    [Fact]
    public void Complete_PartitionsAreIndependent()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);
        tracker.Track(0, 0);
        tracker.Track(1, 0);

        tracker.Hold(0, 0);

        tracker.Complete(1, 0).Should().Be(1);
    }

    [Fact]
    public void Complete_AfterRevoke_ReturnsNull()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);
        tracker.Track(0, 0);

        tracker.Revoke([0]);

        tracker.Complete(0, 0).Should().BeNull();
        stored.Should().BeEmpty();
    }

    [Fact]
    public void Hold_AfterRevoke_DoesNotAffectNewAssignment()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);
        tracker.Track(0, 5);

        tracker.Revoke([0]);

        tracker.Hold(0, 5).Should().BeFalse();
        tracker.Track(0, 7);
        tracker.Complete(0, 7).Should().Be(8);
    }

    [Fact]
    public void Complete_UntrackedOffset_ReturnsNull()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);

        tracker.Complete(0, 5).Should().BeNull();
        stored.Should().BeEmpty();
    }

    [Fact]
    public void TrackedCount_WithOutOfOrderCompletions_CountsEachOffsetOnce()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);
        TrackRange(tracker, 0, 0, 2);

        tracker.Complete(0, 2);
        tracker.TrackedCount(0).Should().Be(3);

        tracker.Complete(0, 1);
        tracker.TrackedCount(0).Should().Be(3);

        tracker.Complete(0, 0);
        tracker.TrackedCount(0).Should().Be(0);
    }

    [Fact]
    public void Track_AtLimit_SignalsBackPressureWithoutHolding()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 2, stored);

        tracker.Track(0, 0).Should().BeFalse();
        tracker.Track(0, 1).Should().BeTrue();
        tracker.HasCapacity(0).Should().BeFalse();

        tracker.Complete(0, 0).Should().Be(1);
        tracker.HasCapacity(0).Should().BeTrue();
    }

    [Fact]
    public void Track_BeyondHardCeiling_HoldsAtFirstUntrackedOffset()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 2, stored);

        tracker.Track(0, 0).Should().BeFalse();
        tracker.Track(0, 1).Should().BeTrue();
        tracker.Track(0, 2).Should().BeTrue();
        tracker.Track(0, 3).Should().BeTrue();

        // Hard ceiling (2 x max = 4) reached: offset 4 is not tracked, the commit is pinned there.
        tracker.Track(0, 4).Should().BeTrue();

        for (long o = 0; o <= 3; o++)
        {
            tracker.Complete(0, o);
        }

        stored[^1].Should().Be(4);
        tracker.Complete(0, 4).Should().BeNull();
        stored.Should().OnlyContain(p => p <= 4);
    }

    [Fact]
    public void Track_NonMonotonicOffset_HoldsAtThatOffset()
    {
        List<long> stored = [];
        KafkaPartitionCommitTracker tracker = Create(max: 16, stored);
        tracker.Track(0, 5);
        tracker.Track(0, 6);

        tracker.Track(0, 3);

        tracker.TrackedCount(0).Should().Be(0);
        tracker.Complete(0, 5).Should().BeNull();
        stored.Should().BeEmpty();
    }

    [Fact]
    public void Complete_ConcurrentSettlers_EveryStoredPositionCoversOnlyCompletedOffsets()
    {
        const int count = 1000;
        ConcurrentDictionary<long, byte> started = new();
        List<long> stored = [];
        List<string> violations = [];
        long previous = 0;

        KafkaPartitionCommitTracker tracker = new(
            "t",
            1024,
            tpo =>
            {
                // Invoked under the partition lock, so the order of stores is observable here.
                long position = tpo.Offset.Value;
                for (long o = 0; o < position; o++)
                {
                    if (!started.ContainsKey(o))
                    {
                        violations.Add($"position {position} covers unstarted offset {o}");
                        break;
                    }
                }

                if (position <= previous)
                {
                    violations.Add($"position {position} did not advance past {previous}");
                }

                previous = position;
                stored.Add(position);
            });

        for (long o = 0; o < count; o++)
        {
            tracker.Track(0, o);
        }

        long[] permutation = new long[count];
        for (int i = 0; i < count; i++)
        {
            permutation[i] = i;
        }

        Random.Shared.Shuffle(permutation);

        Parallel.ForEach(permutation, o =>
        {
            started.TryAdd(o, 0);
            tracker.Complete(0, o);
        });

        violations.Should().BeEmpty();
        stored[^1].Should().Be(count);
        tracker.TrackedCount(0).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(KafkaPartitionCommitTracker.MaxTrackedPerPartitionCeiling + 1)]
    public void Ctor_CapacityOutOfRange_Throws(int capacity)
    {
        Action act = () => _ = new KafkaPartitionCommitTracker("t", capacity, _ => { });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
