namespace BareWire.Abstractions.Serialization;

/// <summary>
/// Optional capability of an <see cref="IMessageSerializer"/>: adds transport headers to an outbound
/// message after the framework headers and the trace context have been written.
/// </summary>
/// <remarks>
/// <para>
/// The bus checks whether the serializer resolved for a message type implements this interface and, if so,
/// calls <see cref="ContributeHeaders"/> as the last header step — after user headers, framework headers
/// (<c>BW-MessageType</c>, <c>message-id</c>, <c>BW-Exchange</c>) and the injected trace context
/// (<c>traceparent</c>, <c>tracestate</c>, <c>baggage</c>). The contributor therefore sees the final
/// <c>traceparent</c> value.
/// </para>
/// <para>
/// Contract: implementations may only add their own headers. They must not remove or modify headers they did
/// not create (including <c>BW-*</c>, <c>message-id</c> and <c>traceparent</c>). This is a documented
/// contract and is not enforced at runtime.
/// </para>
/// <para>
/// Implementations must be thread-safe and must not throw. The default raw serializers do not implement
/// this interface (raw-first): it exists for explicit opt-in wire formats. Example: the MassTransit envelope
/// serializer copies <c>traceparent</c> to <c>MT-Activity-Id</c> so MassTransit consumers continue the trace.
/// </para>
/// </remarks>
public interface IOutboundHeaderContributor
{
    /// <summary>
    /// Adds this serializer's own headers to the outbound message headers.
    /// </summary>
    /// <param name="headers">
    /// The mutable outbound headers, already containing framework headers and trace context.
    /// Only add new entries; do not remove or modify existing ones.
    /// </param>
    void ContributeHeaders(IDictionary<string, string> headers);
}
