namespace BareWire.Outbox;

/// <summary>
/// Derives the nack-deferral schedule for outbox rows from the store's <see cref="OutboxOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Base schedule.</b> The first nack of a row defers its next dispatch attempt by
/// <c>PollingInterval</c>. Every subsequent nack doubles the previous base deferral, capped at
/// <c>OutboxLockTimeout</c>. For example, with <c>PollingInterval</c> = 1 s and
/// <c>OutboxLockTimeout</c> = 30 s the base buckets are 1, 2, 4, 8, 16, 30 seconds.
/// </para>
/// <para>
/// <b>RetryCount semantics.</b> The <c>retryCount</c> passed to this schedule is the number of
/// nacks a row has accumulated so far, i.e. the value <i>before</i> the current nack. Expiry of a
/// row's claim after a process crash does not increment <c>RetryCount</c> — only an explicit nack
/// does. A row's base deferral is looked up by clamping <c>retryCount</c> to the last bucket index,
/// so any retry count at or beyond the escalation ceiling — including <see cref="int.MaxValue"/> —
/// resolves to the <c>OutboxLockTimeout</c> bucket without shifting or overflowing.
/// </para>
/// <para>
/// <b>Stratified jitter.</b> A per-release <see cref="OutboxNackDeferralPlan"/> adds jitter of up
/// to +20% on top of the base deferral, never below <c>PollingInterval</c>, with a maximum of up to
/// 1.2x <c>OutboxLockTimeout</c> (jitter is added on top of the cap, not truncated to it, so the
/// spread survives for rows that are permanently at the escalation ceiling). Within one row of the
/// deferral matrix the cells are non-decreasing across jitter buckets, and the two extreme buckets
/// (bucket 0 and bucket <see cref="JitterBucketCount"/> − 1) always differ by more than 10% of the
/// base deferral — regardless of the values drawn from the jitter source, and including tick-scale
/// base deferrals, because the last bucket's jitter is rounded up to a whole tick while the others
/// are rounded down (so the +20% bound may be exceeded by less than one tick) — so rows that were nacked
/// together in the same release do not all return for redispatch in a single wave. On the very
/// first nacks, where the base deferral equals <c>PollingInterval</c>, the resulting spread is
/// smaller than one polling step; the spread only becomes meaningful for rows past the low
/// escalation buckets. Bucket 0 (rows whose id is a multiple of <see cref="JitterBucketCount"/>)
/// always returns first among the buckets of a row — this bucket-ordering bias is an accepted,
/// documented limitation and not a fairness guarantee.
/// </para>
/// <para>
/// <b>Realization shape.</b> The schedule and its plan are designed to be realized by a single
/// <c>ExecuteUpdateAsync</c> call: the new <c>LockedAt</c> value is a nested conditional expression
/// over the row's <c>RetryCount</c> bucket (<c>== 0</c>, <c>== 1</c>, …, otherwise the escalation
/// cap) and <c>Id % JitterBucketCount</c>, with the <see cref="OutboxNackDeferralSchedule.RetryBucketCount"/>
/// x <see cref="JitterBucketCount"/> cell values — obtained from
/// <see cref="OutboxNackDeferralPlan.GetDeferredLockedAt(DateTimeOffset, int, int)"/> as
/// <see cref="DateTimeOffset"/> values — passed as query <b>parameters</b>, not inlined as constant
/// expressions. Inlining would bloat the provider's query plan cache with one entry per release
/// (each release draws different jitter), whereas parameters keep the translated <c>CASE</c>
/// expression provider-agnostic and its SQL shape stable across releases. Each cell's
/// <c>LockedAt = now − OutboxLockTimeout + deferral</c> makes the row claimable again only once the
/// deferral has elapsed, under the existing claim predicate <c>LockedAt &lt; now' − OutboxLockTimeout</c>.
/// </para>
/// </remarks>
internal sealed class OutboxNackDeferralSchedule
{
    /// <summary>Number of jitter buckets a row is stratified into via <c>Id % JitterBucketCount</c>.</summary>
    internal const int JitterBucketCount = 4;

    /// <summary>Maximum fraction of the base deferral that jitter may add on top of it.</summary>
    internal const double MaxJitterFraction = 0.2;

    private readonly TimeSpan[] _baseDeferrals;
    private readonly IOutboxJitterSource _jitterSource;

    /// <summary>
    /// Creates a schedule from explicit polling interval and lock timeout values.
    /// </summary>
    /// <param name="pollingInterval">The store's polling interval; must be greater than zero.</param>
    /// <param name="lockTimeout">The store's lock timeout; must be greater than or equal to <paramref name="pollingInterval"/>.</param>
    /// <param name="jitterSource">The source of per-plan jitter draws.</param>
    internal OutboxNackDeferralSchedule(TimeSpan pollingInterval, TimeSpan lockTimeout, IOutboxJitterSource jitterSource)
    {
        ArgumentNullException.ThrowIfNull(jitterSource);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollingInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(lockTimeout, pollingInterval);

        PollingInterval = pollingInterval;
        LockTimeout = lockTimeout;
        _jitterSource = jitterSource;
        _baseDeferrals = BuildBaseDeferrals(pollingInterval, lockTimeout);
    }

    /// <summary>The store's polling interval — also the first nack's base deferral and the floor for every deferral.</summary>
    internal TimeSpan PollingInterval { get; }

    /// <summary>The store's lock timeout — the cap for the base (pre-jitter) deferral.</summary>
    internal TimeSpan LockTimeout { get; }

    /// <summary>Number of distinct base-deferral buckets, i.e. the number of escalation steps plus one.</summary>
    internal int RetryBucketCount => _baseDeferrals.Length;

    /// <summary>Number of doubling steps between the first bucket (<see cref="PollingInterval"/>) and the last (<see cref="LockTimeout"/>).</summary>
    internal int EscalationStepCount => RetryBucketCount - 1;

    /// <summary>Creates a schedule from an outbox store's options.</summary>
    internal static OutboxNackDeferralSchedule FromOptions(OutboxOptions options, IOutboxJitterSource jitterSource)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new OutboxNackDeferralSchedule(options.PollingInterval, options.OutboxLockTimeout, jitterSource);
    }

    /// <summary>Returns the base (pre-jitter) deferral for a row currently at <paramref name="retryCount"/> nacks.</summary>
    internal TimeSpan GetBaseDeferral(int retryCount) => _baseDeferrals[GetRetryBucket(retryCount)];

    /// <summary>Maps a row's current <c>RetryCount</c> to a base-deferral bucket index, clamped at the escalation cap.</summary>
    internal int GetRetryBucket(int retryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retryCount);
        return Math.Min(retryCount, RetryBucketCount - 1);
    }

    /// <summary>Maps a row id to its jitter bucket via <c>Id % JitterBucketCount</c>, normalized to a non-negative result.</summary>
    internal static int GetJitterBucket(long rowId)
    {
        long remainder = rowId % JitterBucketCount;
        return (int)((remainder + JitterBucketCount) % JitterBucketCount);
    }

    /// <summary>
    /// Draws exactly <see cref="JitterBucketCount"/> stratified jitter fractions from the jitter
    /// source (one per jitter bucket) and builds the resulting deferral matrix for one release.
    /// </summary>
    internal OutboxNackDeferralPlan CreatePlan()
    {
        var jitterFractions = new double[JitterBucketCount];
        for (int bucket = 0; bucket < JitterBucketCount; bucket++)
        {
            double draw = _jitterSource.NextDouble();
            double clamped = double.IsNaN(draw) ? 0.0 : Math.Clamp(draw, 0.0, Math.BitDecrement(1.0));
            jitterFractions[bucket] = MaxJitterFraction * (bucket + clamped) / JitterBucketCount;
        }

        return new OutboxNackDeferralPlan(this, jitterFractions);
    }

    private static TimeSpan[] BuildBaseDeferrals(TimeSpan pollingInterval, TimeSpan lockTimeout)
    {
        var buckets = new List<TimeSpan>();
        long current = pollingInterval.Ticks;
        while (current < lockTimeout.Ticks)
        {
            buckets.Add(TimeSpan.FromTicks(current));
            current = current > lockTimeout.Ticks / 2 ? lockTimeout.Ticks : current * 2;
        }

        buckets.Add(lockTimeout);
        return [.. buckets];
    }
}
