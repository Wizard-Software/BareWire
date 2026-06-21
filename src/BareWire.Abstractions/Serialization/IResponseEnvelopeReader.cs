using System.Buffers;

namespace BareWire.Abstractions.Serialization;

/// <summary>
/// Optional extension of <see cref="IMessageDeserializer"/> for deserializers that can extract
/// the request identifier from a response envelope for correlation purposes.
/// </summary>
/// <remarks>
/// <para>
/// Some request-response implementations (e.g. MassTransit) correlate responses to pending
/// requests by echoing a <c>requestId</c> field back into the response envelope, rather than
/// relying solely on the transport-level correlation identifier (e.g. AMQP <c>CorrelationId</c>).
/// The BareWire request-client receive path uses <see cref="TryReadRequestId"/> as a fallback
/// when the transport-level identifier is absent or does not match any known pending request.
/// </para>
/// <para>
/// Implementing this interface is strictly opt-in. Deserializers for raw formats that do not
/// produce envelopes (e.g. plain JSON, MsgPack) do not need to implement it. This design is a
/// non-breaking addition — no existing <see cref="IMessageDeserializer"/> implementation is
/// affected.
/// </para>
/// <para>
/// <b>Thread safety:</b> Implementations must be safe to call from multiple threads concurrently.
/// The method must not allocate on the heap in the common failure path. Implementations should
/// catch format exceptions internally and return <see langword="false"/> rather than propagating
/// them to the caller.
/// </para>
/// </remarks>
public interface IResponseEnvelopeReader
{
    /// <summary>
    /// Attempts to extract the <c>requestId</c> from the envelope contained in <paramref name="body"/>.
    /// </summary>
    /// <param name="body">The raw zero-copy byte sequence containing the response envelope.</param>
    /// <param name="requestId">
    /// When this method returns <see langword="true"/>, contains the request identifier read from
    /// the envelope. When this method returns <see langword="false"/>, the value is <see cref="Guid.Empty"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a valid <see cref="Guid"/> was successfully read from the
    /// envelope's <c>requestId</c> field; <see langword="false"/> if the field is absent, the
    /// value is malformed, or the body cannot be parsed (e.g. malformed JSON).
    /// Implementations must never throw — format errors must be swallowed and reported as
    /// <see langword="false"/>.
    /// </returns>
    bool TryReadRequestId(ReadOnlySequence<byte> body, out Guid requestId);
}
