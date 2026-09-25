namespace BareWire.Outbox;

/// <summary>
/// Immutable per-release deferral matrix (<see cref="RetryBucketCount"/> x <see cref="JitterBucketCount"/>)
/// produced by <see cref="OutboxNackDeferralSchedule.CreatePlan"/>.
/// </summary>
/// <remarks>
/// One plan is created per release call. Its cells are the ready-to-use deferral (and, via
/// <see cref="GetDeferredLockedAt(DateTimeOffset, int, int)"/>, the ready-to-use <c>LockedAt</c>
/// value) for every combination of retry bucket and jitter bucket, computed once in the
/// constructor. Callers realizing the deferral as a single <c>ExecuteUpdateAsync</c> call read the
/// matrix as parameters for a nested conditional expression translated to <c>CASE</c> — see
/// <see cref="OutboxNackDeferralSchedule"/> for the full realization shape.
/// </remarks>
internal sealed class OutboxNackDeferralPlan
{
    private readonly OutboxNackDeferralSchedule _schedule;
    private readonly double[] _jitterFractions;
    private readonly TimeSpan[,] _deferrals;

    /// <summary>
    /// Builds the deferral matrix for one release from a set of already-drawn, stratified jitter
    /// fractions (one per jitter bucket).
    /// </summary>
    internal OutboxNackDeferralPlan(OutboxNackDeferralSchedule schedule, double[] jitterFractions)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(jitterFractions);

        if (jitterFractions.Length != OutboxNackDeferralSchedule.JitterBucketCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jitterFractions),
                jitterFractions.Length,
                $"Expected exactly {OutboxNackDeferralSchedule.JitterBucketCount} jitter fractions.");
        }

        foreach (double fraction in jitterFractions)
        {
            if (!double.IsFinite(fraction) || fraction < 0.0 || fraction > OutboxNackDeferralSchedule.MaxJitterFraction)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(jitterFractions),
                    fraction,
                    $"Jitter fractions must be finite and within [0, {OutboxNackDeferralSchedule.MaxJitterFraction}].");
            }
        }

        _schedule = schedule;
        _jitterFractions = [.. jitterFractions];
        _deferrals = BuildDeferrals(schedule, _jitterFractions);
    }

    /// <summary>Number of retry (base-deferral) buckets in the matrix — mirrors <see cref="OutboxNackDeferralSchedule.RetryBucketCount"/>.</summary>
    internal int RetryBucketCount => _schedule.RetryBucketCount;

    /// <summary>Number of jitter buckets in the matrix.</summary>
    internal int JitterBucketCount => _jitterFractions.Length;

    /// <summary>Returns the precomputed deferral for one matrix cell.</summary>
    internal TimeSpan GetDeferral(int retryBucket, int jitterBucket)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retryBucket);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(retryBucket, RetryBucketCount);
        ArgumentOutOfRangeException.ThrowIfNegative(jitterBucket);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(jitterBucket, JitterBucketCount);

        return _deferrals[retryBucket, jitterBucket];
    }

    /// <summary>Returns the deferral for a specific row, resolving its retry and jitter buckets first.</summary>
    internal TimeSpan GetDeferralForRow(int retryCount, long rowId)
    {
        int retryBucket = _schedule.GetRetryBucket(retryCount);
        int jitterBucket = OutboxNackDeferralSchedule.GetJitterBucket(rowId);
        return GetDeferral(retryBucket, jitterBucket);
    }

    /// <summary>
    /// Returns the <c>LockedAt</c> value for one matrix cell as of <paramref name="now"/>. This is
    /// the value passed as a query <b>parameter</b> for that cell's branch of the realized
    /// <c>CASE</c> expression — never inlined as a constant, which would produce a distinct SQL
    /// text (and a distinct query-plan-cache entry) for every release.
    /// </summary>
    internal DateTimeOffset GetDeferredLockedAt(DateTimeOffset now, int retryBucket, int jitterBucket)
        => now - _schedule.LockTimeout + GetDeferral(retryBucket, jitterBucket);

    /// <summary>Returns the "not claimable before" <c>LockedAt</c> value for a specific row as of <paramref name="now"/>.</summary>
    internal DateTimeOffset ComputeDeferredLockedAt(DateTimeOffset now, int retryCount, long rowId)
    {
        int retryBucket = _schedule.GetRetryBucket(retryCount);
        int jitterBucket = OutboxNackDeferralSchedule.GetJitterBucket(rowId);
        return GetDeferredLockedAt(now, retryBucket, jitterBucket);
    }

    private static TimeSpan[,] BuildDeferrals(OutboxNackDeferralSchedule schedule, double[] jitterFractions)
    {
        int retryBucketCount = schedule.RetryBucketCount;
        int jitterBucketCount = jitterFractions.Length;
        var deferrals = new TimeSpan[retryBucketCount, jitterBucketCount];

        for (int retryBucket = 0; retryBucket < retryBucketCount; retryBucket++)
        {
            long baseTicks = schedule.GetBaseDeferral(retryBucket).Ticks;
            for (int jitterBucket = 0; jitterBucket < jitterBucketCount; jitterBucket++)
            {
                // Round down for every bucket except the last, which rounds up: ceil(b * f_last) -
                // floor(b * f_0) >= b * (f_last - f_0) > 10% of b, so the extreme-bucket spread
                // survives whole-tick rounding even for tick-scale base deferrals (where flooring
                // every bucket would collapse them into one). The cost is that the last bucket may
                // exceed +20% of the base by less than one tick.
                double scaledJitter = baseTicks * jitterFractions[jitterBucket];
                long jitterTicks = jitterBucket == jitterBucketCount - 1
                    ? (long)Math.Ceiling(scaledJitter)
                    : (long)Math.Floor(scaledJitter);

                // Saturate instead of wrapping: base + jitter must never overflow long.Ticks range.
                jitterTicks = Math.Min(jitterTicks, long.MaxValue - baseTicks);

                long totalTicks = Math.Max(schedule.PollingInterval.Ticks, baseTicks + jitterTicks);
                deferrals[retryBucket, jitterBucket] = TimeSpan.FromTicks(totalTicks);
            }
        }

        return deferrals;
    }
}
