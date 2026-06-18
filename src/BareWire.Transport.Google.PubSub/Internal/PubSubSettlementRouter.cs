using BareWire.Abstractions;

namespace BareWire.Transport.Google.PubSub.Internal;

/// <summary>
/// The Google Cloud Pub/Sub operation that corresponds to a given <see cref="SettlementAction"/>.
/// </summary>
internal enum PubSubSettlementOperation
{
    /// <summary>
    /// Acknowledge the message — permanent removal from the subscription (Ack).
    /// Pub/Sub will not redeliver an acknowledged message.
    /// </summary>
    Acknowledge,

    /// <summary>
    /// Modify the ack deadline to zero seconds — makes the message immediately visible for
    /// redelivery (Nack/Requeue/Defer). This is the Pub/Sub idiom for "nack".
    /// </summary>
    ModifyAckDeadlineZero,

    /// <summary>
    /// Do not acknowledge and do not modify ack deadline — leave the message to be redelivered
    /// until <c>max_delivery_attempts</c> is exhausted and <c>DeadLetterPolicy</c> routes it
    /// to the dead-letter topic (Reject). Full DLQ wiring in R5.3.
    /// </summary>
    DeadLetterViaPolicy,
}

/// <summary>
/// Pure decision logic that maps a <see cref="SettlementAction"/> to the corresponding
/// <see cref="PubSubSettlementOperation"/> for Pub/Sub messages.
/// Zero I/O — fully deterministic and unit-testable in isolation.
/// </summary>
/// <remarks>
/// <para>
/// Mapping rationale (ADR-017: Pub/Sub settlement semantics):
/// <list type="bullet">
/// <item><term>Ack</term><description>→ <see cref="PubSubSettlementOperation.Acknowledge"/>: processed; remove from subscription permanently.</description></item>
/// <item><term>Nack</term><description>→ <see cref="PubSubSettlementOperation.ModifyAckDeadlineZero"/>: release for immediate redelivery.</description></item>
/// <item><term>Requeue</term><description>→ <see cref="PubSubSettlementOperation.ModifyAckDeadlineZero"/>: same as Nack — release for immediate redelivery.</description></item>
/// <item><term>Defer</term><description>→ <see cref="PubSubSettlementOperation.ModifyAckDeadlineZero"/>: release deadline (Pub/Sub has no defer-by-offset).</description></item>
/// <item><term>Reject</term><description>→ <see cref="PubSubSettlementOperation.DeadLetterViaPolicy"/>: do NOT ack; let <c>max_delivery_attempts</c> exhaust → dead-letter topic via <c>DeadLetterPolicy</c> (full wiring R5.3).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Why Reject does NOT call AcknowledgeAsync:</b> acknowledging on the source subscription
/// would permanently discard the message without it ever reaching the dead-letter topic.
/// Instead, leaving the message unacknowledged (no-op) causes Pub/Sub to redeliver it;
/// once <c>max_delivery_attempts</c> is exhausted, <c>DeadLetterPolicy</c> routes it
/// automatically — mirroring SQS RedrivePolicy behaviour (ADR-017).
/// </para>
/// </remarks>
internal static class PubSubSettlementRouter
{
    /// <summary>
    /// Maps a <see cref="SettlementAction"/> to the corresponding <see cref="PubSubSettlementOperation"/>.
    /// </summary>
    /// <param name="action">The settlement action requested by the consumer pipeline.</param>
    /// <returns>The Pub/Sub operation to perform.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="action"/> is not a recognised <see cref="SettlementAction"/> value.
    /// </exception>
    internal static PubSubSettlementOperation Map(SettlementAction action) =>
        action switch
        {
            SettlementAction.Ack => PubSubSettlementOperation.Acknowledge,
            SettlementAction.Nack => PubSubSettlementOperation.ModifyAckDeadlineZero,
            SettlementAction.Requeue => PubSubSettlementOperation.ModifyAckDeadlineZero,
            SettlementAction.Defer => PubSubSettlementOperation.ModifyAckDeadlineZero,
            SettlementAction.Reject => PubSubSettlementOperation.DeadLetterViaPolicy,
            _ => throw new ArgumentOutOfRangeException(
                nameof(action), action, $"Unknown SettlementAction value: {action}."),
        };
}
