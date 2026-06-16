using BareWire.Abstractions;

namespace BareWire.Transport.Kafka.Internal;

/// <summary>
/// The outcome of routing a <see cref="SettlementAction"/> through the retry/DLQ pattern.
/// </summary>
internal enum SettlementOutcome
{
    /// <summary>Store the source offset (Ack — message processed).</summary>
    StoreOffset,

    /// <summary>Do not store the offset (Nack/Requeue below the retry cap — replay from last commit).</summary>
    NoStore,

    /// <summary>Republish to the retry-topic with backoff, then store the source offset.</summary>
    RepublishRetryThenStore,

    /// <summary>Republish to the DLQ-topic, then store the source offset.</summary>
    RepublishDlqThenStore,
}

/// <summary>
/// Pure decision logic for the retry-topic + DLQ-topic pattern (R1.3). Given a
/// <see cref="SettlementAction"/> and the (clamped) current retry count, decides the
/// <see cref="SettlementOutcome"/>. Zero I/O — fully deterministic and unit-testable in isolation.
/// See ADR-010 for the mapping rationale (extends ADR-009 §3).
/// </summary>
internal static class KafkaSettlementRouter
{
    /// <summary>
    /// Clamps a wire-supplied retry count to the trusted range <c>[0, maxRetryCount]</c> (SEC-1).
    /// A spoofed <c>BW-RetryCount</c> (e.g. <c>999</c> forcing a premature DLQ, or a negative value
    /// causing an unbounded retry loop) is neutralised here before it can influence routing.
    /// </summary>
    /// <param name="wireRetryCount">The raw retry count parsed from the untrusted wire header.</param>
    /// <param name="maxRetryCount">The configured retry cap (the upper clamp bound).</param>
    /// <returns>The retry count clamped to <c>[0, maxRetryCount]</c>.</returns>
    internal static int ClampRetryCount(int wireRetryCount, int maxRetryCount) =>
        Math.Clamp(wireRetryCount, 0, maxRetryCount);

    /// <summary>
    /// Decides the settlement outcome for an action given the clamped current retry count.
    /// </summary>
    /// <param name="action">The settlement action requested by the consumer pipeline.</param>
    /// <param name="currentRetryCount">The clamped current retry count (see <see cref="ClampRetryCount"/>).</param>
    /// <param name="maxRetryCount">The configured maximum retry count.</param>
    /// <returns>The routing outcome.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an unknown <paramref name="action"/>.</exception>
    internal static SettlementOutcome Decide(SettlementAction action, int currentRetryCount, int maxRetryCount) =>
        action switch
        {
            SettlementAction.Ack => SettlementOutcome.StoreOffset,
            SettlementAction.Requeue => SettlementOutcome.NoStore,

            // Defer: schedule a delayed retry while attempts remain; otherwise dead-letter.
            SettlementAction.Defer when currentRetryCount < maxRetryCount => SettlementOutcome.RepublishRetryThenStore,
            SettlementAction.Defer => SettlementOutcome.RepublishDlqThenStore,

            // Reject: dead-letter immediately, regardless of retry count.
            SettlementAction.Reject => SettlementOutcome.RepublishDlqThenStore,

            // Nack: replay from the last commit while attempts remain; on exhaustion dead-letter
            // to break a poison-message replay loop (poison guard).
            SettlementAction.Nack when currentRetryCount < maxRetryCount => SettlementOutcome.NoStore,
            SettlementAction.Nack => SettlementOutcome.RepublishDlqThenStore,

            _ => throw new ArgumentOutOfRangeException(
                nameof(action), action, $"Unknown SettlementAction value: {action}."),
        };
}
