using System.Buffers;

namespace BareWire.Abstractions.Serialization;

/// <summary>
/// Optional capability for serializers that can write a response envelope echoing the
/// <c>requestId</c> from an inbound request, enabling MassTransit correlation on the
/// response receive path.
/// </summary>
/// <remarks>
/// <para>
/// When a BareWire consumer handles a request that originated from a MassTransit
/// <c>IRequestClient&lt;T&gt;</c>, the response must be wrapped in a
/// <c>application/vnd.masstransit+json</c> envelope that echoes the inbound <c>requestId</c>.
/// MassTransit uses this identifier — not the AMQP <c>correlation_id</c> — to match the
/// response to the pending request.
/// </para>
/// <para>
/// <see cref="ConsumeContext.RespondAsync{T}"/> uses this interface when the context carries
/// an inbound <see cref="RequestEnvelopeContext"/> (i.e. the request arrived via the MT envelope
/// format and the deserializer implements <see cref="IRequestEnvelopeRouteReader"/>). The
/// implementation pre-serializes the response envelope into an <c>ArrayPool</c> buffer and
/// sends it via <see cref="ISendEndpoint.SendRawAsync"/> — no new overloads are added to
/// <see cref="ISendEndpoint"/>.
/// </para>
/// <para>
/// Implementing this interface is strictly opt-in. This is a non-breaking addition — no
/// existing serializer is affected.
/// </para>
/// <para>
/// <b>Thread safety:</b> Implementations must be safe to call from multiple threads concurrently.
/// Output is written directly to the supplied buffer writer with no intermediate heap allocation
/// in the common path (ADR-003).
/// </para>
/// </remarks>
public interface IResponseEnvelopeWriter
{
    /// <summary>
    /// Writes a response envelope for <paramref name="response"/>, echoing the supplied
    /// <paramref name="requestId"/> so that the remote request client can correlate the reply.
    /// </summary>
    /// <typeparam name="T">The response message type. Must be a reference type.</typeparam>
    /// <param name="response">The response message instance to serialize. Must not be null.</param>
    /// <param name="requestId">
    /// The request identifier extracted from the inbound request envelope. This value is written
    /// into the response envelope's <c>requestId</c> field verbatim — no transformation is applied.
    /// Must not be <see cref="Guid.Empty"/>.
    /// </param>
    /// <param name="output">
    /// The buffer writer to write the serialized response envelope bytes into. Must not be null.
    /// </param>
    void WriteResponse<T>(T response, Guid requestId, IBufferWriter<byte> output) where T : class;
}
