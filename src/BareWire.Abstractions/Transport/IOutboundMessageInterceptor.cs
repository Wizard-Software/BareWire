namespace BareWire.Abstractions.Transport;

/// <summary>
/// Intercepts outbound messages produced by <see cref="IPublishEndpoint"/> and
/// <see cref="ISendEndpoint"/> after serialization and before they are queued for the transport.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trust boundary.</b> An implementation that returns <see langword="true"/> from
/// <see cref="TryIntercept"/> takes over responsibility for delivering the message: the bus will not
/// queue it for the transport, will not count it against the publish byte budget, and will not record
/// it as a regular publish. A message that is captured and never delivered is silently lost.
/// </para>
/// <para>
/// <b>Single slot.</b> The bus consults at most one interceptor. Registering another implementation
/// replaces the previous one (for example, the transactional outbox). When more than one implementation
/// is registered, the last registration wins and a warning is logged.
/// </para>
/// <para>
/// Implementations are singletons and must be thread-safe. They are invoked synchronously on the
/// caller's asynchronous flow, so an implementation may consult ambient (<c>AsyncLocal</c>) state such
/// as an active transactional outbox buffer. Implementations must not block or perform I/O.
/// </para>
/// </remarks>
public interface IOutboundMessageInterceptor
{
    /// <summary>
    /// Gets a value indicating whether this interceptor is currently willing to capture outbound messages.
    /// </summary>
    /// <remarks>
    /// A cheap, side-effect-free hint queried on the caller's asynchronous flow before
    /// <see cref="TryIntercept"/>. When <see langword="false"/> the bus skips interception entirely and
    /// delivers the message as usual. When <see langword="true"/> the bus may allocate an owned copy of the
    /// body for borrowed (raw) payloads before calling <see cref="TryIntercept"/>.
    /// </remarks>
    bool IsCapturing { get; }

    /// <summary>
    /// Attempts to take ownership of <paramref name="message"/>.
    /// </summary>
    /// <param name="message">
    /// The serialized outbound message. For raw publish/send paths the body is an owned copy of the
    /// caller's payload (made only when <see cref="IsCapturing"/> is <see langword="true"/>), so it is
    /// safe to retain.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the message was captured and must not be queued for the transport;
    /// <see langword="false"/> to let the bus deliver the message as usual.
    /// </returns>
    bool TryIntercept(OutboundMessage message);
}
