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
    /// Nacked rows (partial send failures) are not explicitly unlocked. They become
    /// eligible for re-delivery once this timeout elapses. To minimize retry latency,
    /// set this value conservatively relative to your broker's worst-case publish-confirm time.
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
