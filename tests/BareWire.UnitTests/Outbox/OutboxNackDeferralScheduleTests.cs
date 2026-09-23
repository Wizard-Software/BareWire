using AwesomeAssertions;
using BareWire.Outbox;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Outbox;

/// <summary>
/// Unit tests for <see cref="OutboxNackDeferralSchedule"/> and <see cref="OutboxNackDeferralPlan"/>:
/// base-deferral escalation, escalation step counts, stratified jitter, and deferred "not claimable
/// before" LockedAt computation.
/// </summary>
public sealed class OutboxNackDeferralScheduleTests
{
    private static readonly TimeSpan P = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan L = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedJitterSource(params double[] values) : IOutboxJitterSource
    {
        private int _next;

        public int Calls { get; private set; }

        public double NextDouble()
        {
            Calls++;
            return values[_next++ % values.Length];
        }
    }

    public static TheoryData<double> AnyJitterDraws => new()
    {
        0.0, 0.5, 0.999999, -3.0, 7.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, Math.BitDecrement(1.0),
    };

    public static TheoryData<double> MaximumJitterDraws => new() { 7.0, Math.BitDecrement(1.0) };

    // ── Deliverable A: base schedule (escalation, cap, escalation step counts) ─────────────────

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(6, 30)]
    [InlineData(1000, 30)]
    [InlineData(int.MaxValue, 30)]
    public void GetBaseDeferral_RetryCount_DoublesFromPollingIntervalUpToLockTimeout(int retryCount, int expectedSeconds)
    {
        var schedule = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(0.0));
        schedule.GetBaseDeferral(retryCount).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void GetBaseDeferral_NegativeRetryCount_ThrowsArgumentOutOfRange()
    {
        var schedule = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(0.0));
        Action act = () => schedule.GetBaseDeferral(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RetryBucketCount_DefaultOptions_IsSixWithFiveEscalationSteps()
    {
        var schedule = OutboxNackDeferralSchedule.FromOptions(OutboxOptions.Default, new FixedJitterSource(0.0));
        schedule.RetryBucketCount.Should().Be(6);
        schedule.EscalationStepCount.Should().Be(5);
    }

    [Theory]
    [InlineData(1_000, 3_000)]
    [InlineData(500, 1_600)]
    [InlineData(2_000, 7_000)]
    [InlineData(1_000, 30_000)]
    [InlineData(250, 60_000)]
    public void FromOptions_AnyConfigurationPassingValidation_YieldsAtLeastTwoEscalationSteps(int pollingMs, int lockMs)
    {
        var options = OutboxOptions.Default with
        {
            PollingInterval = TimeSpan.FromMilliseconds(pollingMs),
            OutboxLockTimeout = TimeSpan.FromMilliseconds(lockMs),
        };
        options.Validate(); // must not throw — the lock timeout >= 3 x polling interval rule holds

        var schedule = OutboxNackDeferralSchedule.FromOptions(options, new FixedJitterSource(0.0));

        schedule.EscalationStepCount.Should().BeGreaterThanOrEqualTo(2);
        schedule.GetBaseDeferral(0).Should().Be(options.PollingInterval);
        schedule.GetBaseDeferral(1).Should().Be(2 * options.PollingInterval);
        schedule.GetBaseDeferral(int.MaxValue).Should().Be(options.OutboxLockTimeout);
        for (int r = 1; r < schedule.RetryBucketCount; r++)
        {
            schedule.GetBaseDeferral(r).Should().BeGreaterThan(schedule.GetBaseDeferral(r - 1));
        }
    }

    [Fact]
    public void FromOptions_MinimumValidLockTimeout_ProducesPollingDoubleAndCapSteps()
    {
        var options = OutboxOptions.Default with { OutboxLockTimeout = 3 * OutboxOptions.Default.PollingInterval };
        options.Validate();
        var schedule = OutboxNackDeferralSchedule.FromOptions(options, new FixedJitterSource(0.0));
        Enumerable.Range(0, 4).Select(schedule.GetBaseDeferral).Should().Equal(P, 2 * P, 3 * P, 3 * P);
    }

    [Fact]
    public void Constructor_LockTimeoutBelowPollingInterval_ThrowsArgumentOutOfRange()
    {
        Action act = () => _ = new OutboxNackDeferralSchedule(P, P - TimeSpan.FromTicks(1), new FixedJitterSource(0.0));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_NonPositivePollingInterval_ThrowsArgumentOutOfRange()
    {
        Action act = () => _ = new OutboxNackDeferralSchedule(TimeSpan.Zero, L, new FixedJitterSource(0.0));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_NullJitterSource_ThrowsArgumentNull()
    {
        Action act = () => _ = new OutboxNackDeferralSchedule(P, L, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_LockTimeoutEqualToPollingInterval_HasSingleBucketAtPollingInterval()
    {
        var schedule = new OutboxNackDeferralSchedule(P, P, new FixedJitterSource(0.0));
        schedule.RetryBucketCount.Should().Be(1);
        schedule.GetBaseDeferral(0).Should().Be(P);
    }

    // ── Deliverable B: jitter plan (stratification, floor, spread, time-based claimability) ────

    [Fact]
    public void CreatePlan_DrawsExactlyOneJitterValuePerBucket()
    {
        var source = new FixedJitterSource(0.5);
        new OutboxNackDeferralSchedule(P, L, source).CreatePlan();
        source.Calls.Should().Be(OutboxNackDeferralSchedule.JitterBucketCount);
    }

    [Fact]
    public void GetDeferral_ZeroJitterDraw_FirstNackInFirstBucketEqualsPollingInterval()
    {
        var plan = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(0.0)).CreatePlan();
        plan.GetDeferral(retryBucket: 0, jitterBucket: 0).Should().Be(P);
    }

    [Theory]
    [MemberData(nameof(AnyJitterDraws))]
    public void GetDeferral_AnyJitterDraw_StaysWithinBaseAndTwentyPercentAboveIt(double draw)
    {
        var schedule = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(draw));
        var plan = schedule.CreatePlan();

        for (int r = 0; r < plan.RetryBucketCount; r++)
        {
            for (int j = 0; j < plan.JitterBucketCount; j++)
            {
                TimeSpan baseDeferral = schedule.GetBaseDeferral(r);
                TimeSpan deferral = plan.GetDeferral(r, j);
                TimeSpan maxAllowed = baseDeferral +
                    TimeSpan.FromTicks((long)Math.Ceiling(baseDeferral.Ticks * OutboxNackDeferralSchedule.MaxJitterFraction));

                deferral.Should().BeGreaterThanOrEqualTo(baseDeferral).And.BeGreaterThanOrEqualTo(P);
                deferral.Should().BeLessThanOrEqualTo(maxAllowed);
            }
        }
    }

    [Theory]
    [MemberData(nameof(MaximumJitterDraws))]
    public void GetDeferral_CappedBucketWithMaximumDraw_DoesNotExceedOnePointTwoLockTimeout(double draw)
    {
        var schedule = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(draw));
        var plan = schedule.CreatePlan();

        TimeSpan deferral = plan.GetDeferral(plan.RetryBucketCount - 1, plan.JitterBucketCount - 1);
        TimeSpan maxAllowed = L + TimeSpan.FromTicks((long)Math.Ceiling(L.Ticks * OutboxNackDeferralSchedule.MaxJitterFraction));

        deferral.Should().BeLessThanOrEqualTo(maxAllowed);
    }

    [Fact]
    public void GetDeferral_SameRetryDifferentJitterBuckets_AreStrictlyIncreasing()
    {
        var plan = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(0.9, 0.0, 0.9, 0.0)).CreatePlan();
        for (int j = 1; j < plan.JitterBucketCount; j++)
        {
            plan.GetDeferral(0, j).Should().BeGreaterThan(plan.GetDeferral(0, j - 1));
        }
    }

    [Theory]
    [MemberData(nameof(AnyJitterDraws))]
    public void GetDeferral_ExtremeBucketsAnyDraw_DifferByMoreThanTenPercentOfBase(double draw)
    {
        var schedule = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(draw));
        var plan = schedule.CreatePlan();

        for (int r = 0; r < plan.RetryBucketCount; r++)
        {
            TimeSpan baseDeferral = schedule.GetBaseDeferral(r);
            TimeSpan lowest = plan.GetDeferral(r, 0);
            TimeSpan highest = plan.GetDeferral(r, plan.JitterBucketCount - 1);

            (highest - lowest).Should().BeGreaterThan(TimeSpan.FromTicks((long)(baseDeferral.Ticks * 0.1)));
        }
    }

    [Theory]
    [InlineData(1L, 0.0)]
    [InlineData(1L, 0.999)]
    [InlineData(2L, 0.5)]
    [InlineData(3L, 0.999)]
    [InlineData(7L, 0.0)]
    [InlineData(10L, 0.999)]
    [InlineData(19L, 0.5)]
    public void GetDeferral_TickScalePollingInterval_ExtremeBucketsStillDiffer(long pollingTicks, double draw)
    {
        TimeSpan polling = TimeSpan.FromTicks(pollingTicks);
        var schedule = new OutboxNackDeferralSchedule(polling, TimeSpan.FromTicks(3 * pollingTicks), new FixedJitterSource(draw));
        var plan = schedule.CreatePlan();

        for (int r = 0; r < plan.RetryBucketCount; r++)
        {
            TimeSpan baseDeferral = schedule.GetBaseDeferral(r);
            TimeSpan lowest = plan.GetDeferral(r, 0);
            TimeSpan highest = plan.GetDeferral(r, plan.JitterBucketCount - 1);

            (highest - lowest).Ticks.Should().BeGreaterThan((long)(baseDeferral.Ticks * 0.1));
            highest.Should().BeLessThanOrEqualTo(
                baseDeferral + TimeSpan.FromTicks((long)Math.Ceiling(baseDeferral.Ticks * OutboxNackDeferralSchedule.MaxJitterFraction)));
        }
    }

    [Fact]
    public void CreatePlan_SameDeterministicDraws_ProducesIdenticalMatrices()
    {
        double[] draws = [0.3, 0.6, 0.1, 0.9];
        var planA = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(draws)).CreatePlan();
        var planB = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(draws)).CreatePlan();

        for (int r = 0; r < planA.RetryBucketCount; r++)
        {
            for (int j = 0; j < planA.JitterBucketCount; j++)
            {
                planB.GetDeferral(r, j).Should().Be(planA.GetDeferral(r, j));
            }
        }
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(1L, 1)]
    [InlineData(4L, 0)]
    [InlineData(7L, 3)]
    [InlineData(-1L, 3)]
    [InlineData(long.MaxValue, 3)]
    [InlineData(long.MinValue, 0)]
    public void GetJitterBucket_RowId_ReturnsNonNegativeIdModuloBucketCount(long rowId, int expected)
        => OutboxNackDeferralSchedule.GetJitterBucket(rowId).Should().Be(expected);

    [Fact]
    public void ComputeDeferredLockedAt_FirstNack_RowClaimableOnlyAfterDeferralElapses()
    {
        var clock = new FakeTimeProvider(T0);
        var plan = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(0.0)).CreatePlan();
        DateTimeOffset lockedAt = plan.ComputeDeferredLockedAt(clock.GetUtcNow(), retryCount: 0, rowId: 4);

        bool Claimable() => lockedAt < clock.GetUtcNow() - L; // existing claim predicate
        Claimable().Should().BeFalse();
        clock.Advance(P);
        Claimable().Should().BeFalse();
        clock.Advance(TimeSpan.FromTicks(1));
        Claimable().Should().BeTrue();
    }

    [Fact]
    public void ComputeDeferredLockedAt_RowsRejectedTogether_DoNotReturnInSingleWave()
    {
        var clock = new FakeTimeProvider(T0);
        var plan = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(0.5)).CreatePlan();
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset[] lockedAt = Enumerable.Range(1, 8)
            .Select(id => plan.ComputeDeferredLockedAt(now, retryCount: 0, rowId: id)).ToArray();

        (lockedAt.Max() - lockedAt.Min()).Should().BeGreaterThan(TimeSpan.Zero);
        lockedAt.Distinct().Count().Should().BeGreaterThanOrEqualTo(2);

        clock.Advance(lockedAt.Min() + L - now + TimeSpan.FromTicks(1));
        int claimable = lockedAt.Count(t => t < clock.GetUtcNow() - L);
        claimable.Should().BeGreaterThan(0).And.BeLessThan(lockedAt.Length);
    }

    [Fact]
    public void GetDeferralForRow_RetryCountBeyondCap_UsesCapBucketWithRowJitter()
    {
        var schedule = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(0.4));
        var plan = schedule.CreatePlan();
        const long RowId = 7;
        TimeSpan expected = plan.GetDeferral(plan.RetryBucketCount - 1, OutboxNackDeferralSchedule.GetJitterBucket(RowId));

        plan.GetDeferralForRow(retryCount: 50, RowId).Should().Be(expected);
    }

    [Fact]
    public void GetDeferral_IndexOutOfRange_ThrowsArgumentOutOfRange()
    {
        var plan = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(0.0)).CreatePlan();

        Action retryOutOfRange = () => plan.GetDeferral(plan.RetryBucketCount, 0);
        Action jitterOutOfRange = () => plan.GetDeferral(0, -1);

        retryOutOfRange.Should().Throw<ArgumentOutOfRangeException>();
        jitterOutOfRange.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── GetDeferredLockedAt cell accessor (verification M5) ─────────────────────────────────────

    [Fact]
    public void GetDeferredLockedAt_Cell_EqualsNowMinusLockTimeoutPlusDeferral()
    {
        var clock = new FakeTimeProvider(T0);
        var schedule = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(0.3));
        var plan = schedule.CreatePlan();
        DateTimeOffset now = clock.GetUtcNow();

        const int RetryBucket = 2;
        const int JitterBucket = 1;
        DateTimeOffset expected = now - L + plan.GetDeferral(RetryBucket, JitterBucket);

        plan.GetDeferredLockedAt(now, RetryBucket, JitterBucket).Should().Be(expected);

        const long RowId = JitterBucket; // rowId % JitterBucketCount == JitterBucket for a small positive id
        plan.ComputeDeferredLockedAt(now, retryCount: RetryBucket, RowId).Should().Be(expected);
    }

    // ── Overflow saturation (verification M6) ───────────────────────────────────────────────────

    [Fact]
    public void GetDeferral_LockTimeoutNearMaxValue_SaturatesInsteadOfWrapping()
    {
        var schedule = new OutboxNackDeferralSchedule(P, TimeSpan.MaxValue, new FixedJitterSource(0.999999));
        var plan = schedule.CreatePlan();

        TimeSpan baseDeferral = schedule.GetBaseDeferral(schedule.RetryBucketCount - 1);
        TimeSpan deferral = plan.GetDeferral(plan.RetryBucketCount - 1, plan.JitterBucketCount - 1);

        deferral.Should().Be(TimeSpan.MaxValue);
        deferral.Should().BeGreaterThanOrEqualTo(baseDeferral);
    }

    // ── Plan constructor validation (verification M7) ──────────────────────────────────────────

    [Fact]
    public void Constructor_WrongLengthJitterFractions_ThrowsArgumentOutOfRange()
    {
        var schedule = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(0.0));
        Action act = () => _ = new OutboxNackDeferralPlan(schedule, [0.0, 0.05, 0.1]);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_OutOfRangeJitterFraction_ThrowsArgumentOutOfRange()
    {
        var schedule = new OutboxNackDeferralSchedule(P, L, new FixedJitterSource(0.0));
        Action act = () => _ = new OutboxNackDeferralPlan(schedule, [0.0, 0.05, 0.1, 0.25]);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
