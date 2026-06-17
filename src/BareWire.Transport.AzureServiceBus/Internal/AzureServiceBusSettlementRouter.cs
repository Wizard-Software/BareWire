using BareWire.Abstractions;

namespace BareWire.Transport.AzureServiceBus.Internal;

/// <summary>
/// The Azure Service Bus operation that corresponds to a given <see cref="SettlementAction"/>.
/// </summary>
internal enum AzureServiceBusSettlementOperation
{
    /// <summary>Complete the message — remove it permanently from the queue.</summary>
    Complete,

    /// <summary>Abandon the message — release the lock for immediate redelivery.</summary>
    Abandon,

    /// <summary>Dead-letter the message — move it to the dead-letter sub-queue.</summary>
    DeadLetter,

    /// <summary>Defer the message — suspend delivery until explicitly received by sequence number.</summary>
    Defer,
}

/// <summary>
/// Pure decision logic that maps a <see cref="SettlementAction"/> to the corresponding
/// <see cref="AzureServiceBusSettlementOperation"/> for PeekLock receivers.
/// Zero I/O — fully deterministic and unit-testable in isolation.
/// </summary>
/// <remarks>
/// <para>
/// Mapping rationale (D-3 from the implementation plan):
/// <list type="bullet">
/// <item><term>Ack</term><description>→ <see cref="AzureServiceBusSettlementOperation.Complete"/>: message processed; remove from queue.</description></item>
/// <item><term>Nack</term><description>→ <see cref="AzureServiceBusSettlementOperation.Abandon"/>: release lock; broker redelivers (at-least-once).</description></item>
/// <item><term>Requeue</term><description>→ <see cref="AzureServiceBusSettlementOperation.Abandon"/>: same as Nack — immediate redelivery.</description></item>
/// <item><term>Reject</term><description>→ <see cref="AzureServiceBusSettlementOperation.DeadLetter"/>: permanent failure; move to DLQ.</description></item>
/// <item><term>Defer</term><description>→ <see cref="AzureServiceBusSettlementOperation.Defer"/>: suspend until explicitly received by sequence number (R2.3 builds on this).</description></item>
/// </list>
/// </para>
/// </remarks>
internal static class AzureServiceBusSettlementRouter
{
    /// <summary>
    /// Maps a <see cref="SettlementAction"/> to the corresponding
    /// <see cref="AzureServiceBusSettlementOperation"/> for PeekLock receivers.
    /// </summary>
    /// <param name="action">The settlement action requested by the consumer pipeline.</param>
    /// <returns>The Azure Service Bus operation to perform.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="action"/> is not a recognised <see cref="SettlementAction"/> value.
    /// </exception>
    internal static AzureServiceBusSettlementOperation Map(SettlementAction action) =>
        action switch
        {
            SettlementAction.Ack => AzureServiceBusSettlementOperation.Complete,
            SettlementAction.Nack => AzureServiceBusSettlementOperation.Abandon,
            SettlementAction.Requeue => AzureServiceBusSettlementOperation.Abandon,
            SettlementAction.Reject => AzureServiceBusSettlementOperation.DeadLetter,
            SettlementAction.Defer => AzureServiceBusSettlementOperation.Defer,
            _ => throw new ArgumentOutOfRangeException(
                nameof(action), action, $"Unknown SettlementAction value: {action}."),
        };
}
