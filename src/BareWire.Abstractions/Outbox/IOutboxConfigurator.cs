namespace BareWire.Abstractions.Outbox;

/// <summary>
/// Configures the outbox/inbox pattern for reliable message delivery.
/// All messages published inside a consumer handler are buffered and dispatched
/// only after the handler completes successfully, providing at-least-once delivery guarantees.
/// </summary>
public interface IOutboxConfigurator
{
    /// <summary>
    /// Gets or sets how frequently the outbox dispatcher polls for pending messages.
    /// Defaults to <c>1 second</c>.
    /// </summary>
    TimeSpan PollingInterval { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of outbox messages dispatched in a single polling cycle.
    /// Defaults to <c>100</c>.
    /// </summary>
    int DispatchBatchSize { get; set; }

    /// <summary>
    /// Gets or sets how long processed inbox entries are retained before cleanup removes them.
    /// Must be greater than <see cref="InboxLockTimeout"/> to prevent a lock from expiring
    /// before its entry is cleaned up.
    /// Defaults to <c>7 days</c>.
    /// </summary>
    TimeSpan InboxRetention { get; set; }

    /// <summary>
    /// Gets or sets how long delivered outbox entries are retained before cleanup removes them.
    /// Must be greater than <see cref="OutboxLockTimeout"/> to prevent stale locks surviving cleanup.
    /// Defaults to <c>7 days</c>.
    /// </summary>
    TimeSpan OutboxRetention { get; set; }

    /// <summary>
    /// Gets or sets how long an inbox lock is held before a message is considered safe to re-process.
    /// If a consumer crashes mid-processing, the lock expires and the message can be retried.
    /// Defaults to <c>30 seconds</c>.
    /// </summary>
    TimeSpan InboxLockTimeout { get; set; }

    /// <summary>
    /// Gets or sets how long an outbox dispatcher holds an exclusive claim on a row before
    /// the claim is considered stale and another dispatcher instance may re-claim the row.
    /// This enables crash-recovery: if a dispatcher crashes between claiming and delivering
    /// a row, another instance reclaims it after this timeout expires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must be greater than <c>3 * PollingInterval</c> to survive at least one full
    /// poll-publish-confirm cycle. Setting it too low re-introduces duplicate delivery.
    /// </para>
    /// <para>
    /// Must be less than <see cref="OutboxRetention"/> so that stale locks expire well
    /// before the cleanup service removes the row.
    /// </para>
    /// <para>
    /// This timeout governs <b>crash recovery only</b>. Nacked rows (partial send failures) are
    /// <b>not</b> left for this timeout to expire: the dispatcher explicitly releases their claim and
    /// retries them on the next poll cycle (roughly every <see cref="PollingInterval"/>). Size retry
    /// pressure during broker degradation or poison-message scenarios off <see cref="PollingInterval"/>,
    /// not this value.
    /// </para>
    /// Defaults to <c>30 seconds</c>.
    /// </remarks>
    TimeSpan OutboxLockTimeout { get; set; }

    /// <summary>
    /// Gets or sets how frequently the cleanup service removes expired outbox and inbox entries.
    /// Defaults to <c>1 hour</c>.
    /// </summary>
    TimeSpan CleanupInterval { get; set; }

    /// <summary>
    /// When <see langword="true"/>, suppresses the startup fail-fast guard that rejects an EF Core
    /// provider without a matching atomic outbox/inbox dialect. The store then uses a NON-ATOMIC
    /// client-side fallback that is safe ONLY for a single dispatcher instance / testing — it breaks
    /// claim/dedup invariants under multi-instance load. Default: <see langword="false"/>.
    /// </summary>
    bool AllowNonAtomicProvider { get; set; }

    /// <summary>
    /// When <see langword="true"/>, suppresses the startup fail-fast guard that rejects
    /// <see cref="OrderingMode.PerKey"/> on a dialect that does not provide native head-of-line
    /// ordering (i.e. does not override the 5-arg <c>GetClaimSql</c>). With the guard suppressed,
    /// per-key ordering silently degrades to passthrough: messages sharing an ordering key can be
    /// delivered out of order and marked delivered (irreversible). Leave <see langword="false"/>
    /// unless you accept that degradation. Has no effect when <see cref="OrderingMode"/> is
    /// <see cref="OrderingMode.None"/>. Default: <see langword="false"/>.
    /// </summary>
    bool AllowDegradedOrdering { get; set; }

    /// <summary>
    /// When <see langword="true"/>, Outbox/Inbox tables are created automatically at host startup.
    /// Default: <see langword="false"/>.
    /// </summary>
    bool AutoCreateSchema { get; set; }

    /// <summary>
    /// Gets or sets the local dispatch ordering mode for outbox messages.
    /// Defaults to <see cref="OrderingMode.None"/>, which preserves pre-R7.7 behavior exactly.
    /// Set to <see cref="OrderingMode.PerKey"/> to enable head-of-line ordering per key group.
    /// </summary>
    OrderingMode OrderingMode { get; set; }

    /// <summary>
    /// Gets or sets the message header name whose value is promoted to the <c>OrderingKey</c>
    /// column when <see cref="OrderingMode"/> is <see cref="OrderingMode.PerKey"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no default value. When <see cref="OrderingMode"/> is <see cref="OrderingMode.PerKey"/>,
    /// this property MUST be set explicitly — configuration will be rejected otherwise.
    /// </para>
    /// <para>
    /// A value such as <c>correlation-id</c> is only an example; it is not a default.
    /// Choose a header that is stable per aggregate or domain entity: using a header whose
    /// value changes across messages for the same aggregate will scatter them into different
    /// key groups and defeat the ordering guarantee.
    /// </para>
    /// </remarks>
    string OrderingKeyHeaderName { get; set; }
}
