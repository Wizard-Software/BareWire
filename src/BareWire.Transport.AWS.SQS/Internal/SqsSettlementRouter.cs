using BareWire.Abstractions;

namespace BareWire.Transport.AWS.SQS.Internal;

/// <summary>
/// The Amazon SQS operation that corresponds to a given <see cref="SettlementAction"/>.
/// </summary>
internal enum SqsSettlementOperation
{
    /// <summary>
    /// Delete the message from the queue — permanent removal (Ack).
    /// </summary>
    Delete,

    /// <summary>
    /// Change the message visibility timeout to 0 — makes it immediately visible for redelivery (Nack/Requeue/Defer).
    /// </summary>
    ChangeVisibility,

    /// <summary>
    /// Leave the message in the queue without any destructive action — it will be redelivered
    /// until <c>maxReceiveCount</c> is exhausted and the RedrivePolicy routes it to the DLQ (Reject).
    /// </summary>
    DeadLetterViaRedrive,
}

/// <summary>
/// Pure decision logic that maps a <see cref="SettlementAction"/> to the corresponding
/// <see cref="SqsSettlementOperation"/> for SQS messages.
/// Zero I/O — fully deterministic and unit-testable in isolation.
/// </summary>
/// <remarks>
/// <para>
/// Mapping rationale (ADR-014: SQS settlement semantics):
/// <list type="bullet">
/// <item><term>Ack</term><description>→ <see cref="SqsSettlementOperation.Delete"/>: processed; remove from queue permanently.</description></item>
/// <item><term>Nack</term><description>→ <see cref="SqsSettlementOperation.ChangeVisibility"/>: release for immediate redelivery.</description></item>
/// <item><term>Requeue</term><description>→ <see cref="SqsSettlementOperation.ChangeVisibility"/>: same as Nack — release visibility.</description></item>
/// <item><term>Defer</term><description>→ <see cref="SqsSettlementOperation.ChangeVisibility"/>: release visibility for later redelivery (SQS has no defer-by-sequence-number).</description></item>
/// <item><term>Reject</term><description>→ <see cref="SqsSettlementOperation.DeadLetterViaRedrive"/>: do NOT delete; let <c>maxReceiveCount</c> exhaust → DLQ via RedrivePolicy (ADR-014 / GAP-3).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Why Reject does NOT call DeleteMessage:</b> SQS has no native dead-letter API; calling
/// <c>DeleteMessageAsync</c> on the source queue would permanently discard the message without
/// ever touching the DLQ, causing silent data loss. Instead, releasing visibility and letting the
/// broker route to the DLQ via <c>RedrivePolicy</c> mirrors the behaviour of ASB's
/// <c>DeadLetterAsync</c> without requiring BareWire to manage the DLQ queue URL.
/// See ADR-014 for the full rationale.
/// </para>
/// </remarks>
internal static class SqsSettlementRouter
{
    /// <summary>
    /// Maps a <see cref="SettlementAction"/> to the corresponding <see cref="SqsSettlementOperation"/>.
    /// </summary>
    /// <param name="action">The settlement action requested by the consumer pipeline.</param>
    /// <returns>The SQS operation to perform.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="action"/> is not a recognised <see cref="SettlementAction"/> value.
    /// </exception>
    internal static SqsSettlementOperation Map(SettlementAction action) =>
        action switch
        {
            SettlementAction.Ack => SqsSettlementOperation.Delete,
            SettlementAction.Nack => SqsSettlementOperation.ChangeVisibility,
            SettlementAction.Requeue => SqsSettlementOperation.ChangeVisibility,
            SettlementAction.Defer => SqsSettlementOperation.ChangeVisibility,
            SettlementAction.Reject => SqsSettlementOperation.DeadLetterViaRedrive,
            _ => throw new ArgumentOutOfRangeException(
                nameof(action), action, $"Unknown SettlementAction value: {action}."),
        };
}
