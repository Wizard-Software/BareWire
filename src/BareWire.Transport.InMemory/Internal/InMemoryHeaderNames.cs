namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Header name constants used by the in-memory transport's header-stamping strategy: the reserved
/// <c>BW-</c> prefix every publisher-supplied transport header is stripped under (case-insensitively),
/// the authoritative headers <see cref="InMemoryHeaderSet.Stamp"/> stamps itself, the redelivery
/// counter materialized only on the redelivery path, and the two non-prefixed headers
/// (<see cref="MessageId"/>, <see cref="ContentType"/>) whose values come from a dedicated field on
/// the outbound message rather than from the <c>BW-</c> convention.
/// </summary>
internal static class InMemoryHeaderNames
{
    /// <summary>The prefix every publisher-supplied transport header is stripped under (case-insensitive).</summary>
    internal const string ReservedPrefix = "BW-";

    /// <summary>The exchange actually used for routing, stamped authoritatively by <see cref="InMemoryHeaderSet.Stamp"/>.</summary>
    internal const string Exchange = "BW-Exchange";

    /// <summary>The routing key actually used for routing, stamped authoritatively by <see cref="InMemoryHeaderSet.Stamp"/>.</summary>
    internal const string RoutingKey = "BW-RoutingKey";

    /// <summary>
    /// The redelivery counter header, materialized only by <see cref="RedeliveryHeaderOverlay"/> when
    /// a delivery's redelivery count is greater than zero. Never present on a publisher-supplied
    /// header set — <see cref="InMemoryHeaderSet.Stamp"/> strips it, in any casing, like every other
    /// <c>BW-</c>-prefixed header.
    /// </summary>
    internal const string RedeliveryCount = "BW-RedeliveryCount";

    /// <summary>The message identifier: the publisher's non-empty value, or one generated per publish.</summary>
    internal const string MessageId = "message-id";

    /// <summary>The content type: the outbound message's field, when non-empty, wins over the publisher's header.</summary>
    internal const string ContentType = "content-type";

    /// <summary>
    /// The one <c>BW-</c>-prefixed header carried through unchanged from the publisher, as a message
    /// property rather than a stripped transport header — parity with the RabbitMQ adapter, which
    /// carries this header as the AMQP <c>type</c> property and restores it for the consumer.
    /// </summary>
    internal const string MessageType = "BW-MessageType";
}
