namespace BareWire.Abstractions.Headers;

/// <summary>
/// Configures the mapping between BareWire's canonical header names and transport-specific
/// header names. Apply these mappings when integrating with brokers or services that use
/// non-standard or legacy header conventions.
/// </summary>
public interface IHeaderMappingConfigurator
{
    /// <summary>
    /// Maps the BareWire correlation ID header to the specified transport header name.
    /// </summary>
    /// <param name="headerName">
    /// The transport-level header name that carries the correlation identifier
    /// (e.g. <c>"x-correlation-id"</c>).
    /// </param>
    void MapCorrelationId(string headerName);

    /// <summary>
    /// Maps the BareWire message type header to the specified transport header name.
    /// </summary>
    /// <param name="headerName">
    /// The transport-level header name that carries the message type discriminator
    /// (e.g. <c>"x-message-type"</c>).
    /// </param>
    void MapMessageType(string headerName);

    /// <summary>
    /// Adds a custom mapping between a BareWire canonical header and a transport-specific header.
    /// </summary>
    /// <param name="bareWireHeader">The BareWire canonical header name.</param>
    /// <param name="transportHeader">The corresponding transport-level header name.</param>
    void MapHeader(string bareWireHeader, string transportHeader);

    /// <summary>
    /// Controls whether headers that have no explicit mapping are silently dropped on send
    /// and ignored on receive.
    /// </summary>
    /// <remarks>
    /// Distributed-trace headers are the exception in both directions: <c>traceparent</c>, <c>tracestate</c>,
    /// <c>baggage</c> and the MassTransit <c>MT-Activity-Id</c> header are always sent and read, so a trace is
    /// continued across the broker even when unmapped headers are ignored. An explicit mapping of
    /// <c>tracestate</c>, <c>baggage</c> or <c>MT-Activity-Id</c> with <see cref="MapHeader"/> takes precedence
    /// over that default.
    /// </remarks>
    /// <param name="ignore">
    /// <see langword="true"/> to drop unmapped headers (default); <see langword="false"/> to pass
    /// them through verbatim.
    /// </param>
    void IgnoreUnmappedHeaders(bool ignore = true);
}
