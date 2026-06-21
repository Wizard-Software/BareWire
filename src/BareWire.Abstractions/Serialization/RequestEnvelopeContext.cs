namespace BareWire.Abstractions.Serialization;

/// <summary>
/// Immutable per-request routing metadata passed to <see cref="IRequestEnvelopeSerializer"/>
/// when serializing a request message that expects a correlated response.
/// </summary>
/// <remarks>
/// <para>
/// This context carries the fields that the stateless <see cref="IMessageSerializer"/> contract cannot
/// express: addresses computed from the transport's connection data and a per-request identifier used
/// for correlation. These fields map directly to the envelope properties that request-response
/// infrastructure (such as MassTransit) uses to route responses back to the caller.
/// </para>
/// <para>
/// Values are computed once per request-client instance where they are constant (e.g.
/// <see cref="ResponseAddress"/>, <see cref="DestinationAddress"/>), and per-request where they
/// vary (e.g. <see cref="RequestId"/>, <see cref="ExpirationTime"/>).
/// </para>
/// <para>
/// <b>Thread safety:</b> <see cref="RequestEnvelopeContext"/> is a <c>readonly record struct</c> —
/// it is value-typed and immutable, and is therefore inherently thread-safe. Callers must pass it
/// via <c>in</c> to avoid defensive copies on hot paths.
/// </para>
/// </remarks>
/// <param name="ResponseAddress">
/// The transport URI to which the responder must send the reply. When absent, some responders
/// fall back to publishing the response on the message type's exchange, which typically means the
/// response never reaches the caller's server-named reply queue.
/// </param>
/// <param name="DestinationAddress">
/// The transport URI of the endpoint that will consume the request. Required by some responders
/// for routing diagnostics and dead-letter handling.
/// </param>
/// <param name="FaultAddress">
/// The transport URI to which fault messages (unhandled exceptions on the responder side) should
/// be delivered. Commonly the same as <see cref="ResponseAddress"/>.
/// </param>
/// <param name="RequestId">
/// A high-entropy identifier that uniquely identifies this request invocation.
/// The responder echoes this value into the response envelope, and the caller's receive path
/// correlates the incoming response to the pending request using this identifier.
/// </param>
/// <param name="CorrelationId">
/// An optional user-supplied operation identifier that groups related messages across a
/// conversation or business flow. Distinct from <see cref="RequestId"/>, which is always
/// generated per-invocation.
/// </param>
/// <param name="ExpirationTime">
/// The absolute UTC deadline after which the request is considered expired. The transport layer
/// may also express this as an AMQP <c>expiration</c> property (TTL in milliseconds) to enable
/// broker-side message expiry, preventing resource accumulation when callers time out.
/// </param>
public readonly record struct RequestEnvelopeContext(
    string? ResponseAddress,
    string? DestinationAddress,
    string? FaultAddress,
    Guid RequestId,
    Guid? CorrelationId,
    DateTimeOffset? ExpirationTime);
