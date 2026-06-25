namespace BareWire.Abstractions.Transport;

/// <summary>
/// Represents the outcome of a durable park settlement operation.
/// A durable settlement guarantees that the message has been broker-confirmed at the dead-letter
/// exchange before the original delivery is acknowledged on the consumer channel.
/// </summary>
/// <remarks>
/// Consumers of this type (e.g. R8.12 per-key ordering logic) MUST check
/// <see cref="IsDurablyConfirmed"/> before releasing an ordering key. A result with
/// <see cref="IsDurablyConfirmed"/> equal to <see langword="false"/> means the head has
/// NOT been parked durably; the original message was NOT acknowledged and remains at the
/// head of the queue. The ordering key MUST NOT be released in this case.
/// </remarks>
/// <param name="IsDurablyConfirmed">
/// <see langword="true"/> when the re-publication to the dead-letter exchange was broker-confirmed
/// AND the original delivery was successfully acknowledged on the consumer channel.
/// <see langword="false"/> in all error paths — the original message is left unacknowledged.
/// </param>
/// <param name="FailureReason">
/// An opaque, constant category string describing why the settlement failed, or
/// <see langword="null"/> when <see cref="IsDurablyConfirmed"/> is <see langword="true"/>.
/// <para>
/// SECURITY (S1): This value MUST be a constant, opaque category string — for example
/// <c>"publish nack"</c>, <c>"message returned unrouted"</c>,
/// <c>"consumer channel not found"</c>, or <c>"channel unavailable"</c>.
/// It MUST NOT contain any ordering-key value, message body fragment, routing key value,
/// or any other per-message data that could expose message content in logs or error paths.
/// </para>
/// </param>
public sealed record DurableSettlementResult(bool IsDurablyConfirmed, string? FailureReason);
