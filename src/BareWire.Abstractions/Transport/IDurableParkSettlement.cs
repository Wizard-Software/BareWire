namespace BareWire.Abstractions.Transport;

/// <summary>
/// Allows the per-key ordering layer to durably park a poison head by re-publishing it to a
/// dead-letter exchange on a publisher-confirm–enabled channel and only acknowledging the
/// original delivery after the broker confirms the re-publication.
/// Implemented by transport adapters that support publisher confirms and AMQP dead-letter routing.
/// </summary>
/// <remarks>
/// This is an internal coordination protocol between <c>BareWire</c> (Core/R8.12) and transport
/// adapters. It is not part of the public <see cref="ITransportAdapter"/> surface, in the same
/// way that <see cref="IConsumerChannelManager"/> is not public — transports without dead-letter
/// exchange support are not required to implement this interface.
/// <para>
/// The guarantee this interface provides (contract invariant C3) is:
/// the original message is acknowledged on the consumer channel ONLY after the re-publication
/// to the dead-letter exchange has been broker-confirmed. On any failure the original message
/// is left unacknowledged and the head remains at the queue head, preserving per-key ordering.
/// </para>
/// <para>
/// SECURITY (S1/S2): Implementations MUST NOT include ordering-key values, message body
/// fragments, or per-message data in log output or in <see cref="DurableSettlementResult.FailureReason"/>.
/// Only opaque category strings are permitted.
/// </para>
/// </remarks>
internal interface IDurableParkSettlement
{
    /// <summary>
    /// Re-publishes <paramref name="message"/> to <paramref name="deadLetterExchange"/> on a
    /// publisher-confirm–enabled channel and, only after broker confirmation, acknowledges the
    /// original delivery on the consumer channel.
    /// </summary>
    /// <param name="message">
    /// The inbound message to park. Its <c>BW-ConsumerChannelId</c> header is used to resolve
    /// the consumer channel on which to acknowledge the original delivery.
    /// </param>
    /// <param name="deadLetterExchange">
    /// The name of the dead-letter exchange to publish the parked message to.
    /// Supplied by the caller (Core/R8.12) from endpoint configuration — never derived from
    /// message content.
    /// </param>
    /// <param name="deadLetterRoutingKey">
    /// The routing key used when publishing to <paramref name="deadLetterExchange"/>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="DurableSettlementResult"/> whose <see cref="DurableSettlementResult.IsDurablyConfirmed"/>
    /// is <see langword="true"/> when the message was durably parked and the original delivery ACKed,
    /// or <see langword="false"/> when any failure occurred (original delivery is left unacknowledged).
    /// </returns>
    Task<DurableSettlementResult> ParkHeadDurablyAsync(
        InboundMessage message,
        string deadLetterExchange,
        string deadLetterRoutingKey,
        CancellationToken cancellationToken = default);
}
