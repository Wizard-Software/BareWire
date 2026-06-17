namespace BareWire.Abstractions.Transport;

/// <summary>
/// Transport-neutral handle to a scheduled message. Carries the broker sequence number
/// <b>and</b> the opaque destination (queue / entity name) needed to resolve the correct
/// sender when cancelling the message.
/// </summary>
/// <remarks>
/// <para>
/// No broker-specific type leaks through this abstraction — <see cref="SequenceNumber"/>
/// is a plain <see cref="long"/> (Azure Service Bus sequence number) and
/// <see cref="Destination"/> is the queue / entity name already known to the transport.
/// </para>
/// <para>
/// The <see cref="Destination"/> value is an opaque queue identifier generated internally
/// from the endpoint configuration — it is <b>not</b> a security token and should not be
/// treated as such.
/// </para>
/// </remarks>
/// <param name="SequenceNumber">
/// Broker-assigned sequence number uniquely identifying the scheduled message on the
/// target entity. For Azure Service Bus this is the value returned by
/// <c>ServiceBusSender.ScheduleMessageAsync</c>.
/// </param>
/// <param name="Destination">
/// Opaque queue / topic name identifying the entity on which the message was scheduled.
/// Carried in the token so that <see cref="INativeMessageScheduler.CancelScheduledAsync"/>
/// can resolve the correct sender without additional state.
/// </param>
public readonly record struct ScheduledMessageToken(long SequenceNumber, string Destination);
