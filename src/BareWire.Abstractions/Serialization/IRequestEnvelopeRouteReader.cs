using System.Buffers;

namespace BareWire.Abstractions.Serialization;

/// <summary>
/// Optional extension of <see cref="IMessageDeserializer"/> for deserializers that can extract
/// inbound request routing metadata from a request envelope sent by a remote request client
/// (e.g. MassTransit <c>IRequestClient&lt;T&gt;</c>).
/// </summary>
/// <remarks>
/// <para>
/// When MassTransit sends a request, it embeds routing metadata — <c>responseAddress</c>,
/// <c>requestId</c>, and <c>faultAddress</c> — inside the <c>application/vnd.masstransit+json</c>
/// envelope body rather than in transport-level AMQP properties. This interface allows the
/// BareWire consume pipeline to extract those fields so that
/// <see cref="ConsumeContext.RespondAsync{T}"/> can route the reply directly back to the
/// MassTransit reply queue and echo the <c>requestId</c> required for correlation.
/// </para>
/// <para>
/// Implementing this interface is strictly opt-in. Deserializers for raw formats that do not
/// produce envelopes (e.g. plain JSON, MsgPack) do not need to implement it. This design is a
/// non-breaking addition — no existing <see cref="IMessageDeserializer"/> implementation is
/// affected.
/// </para>
/// <para>
/// <b>Thread safety:</b> Implementations must be safe to call from multiple threads concurrently.
/// The method must not allocate on the heap in the common failure path (use
/// <c>Utf8JsonReader.TryGetGuid</c> for <c>requestId</c>, not <c>Guid.TryParse(GetString())</c>).
/// Implementations must catch format exceptions internally and return <see langword="false"/>
/// rather than propagating them to the caller (SEC-3).
/// </para>
/// </remarks>
public interface IRequestEnvelopeRouteReader
{
    /// <summary>
    /// Attempts to extract routing metadata from the request envelope contained in
    /// <paramref name="body"/>.
    /// </summary>
    /// <param name="body">
    /// The raw zero-copy byte sequence containing the inbound request envelope.
    /// The sequence is valid only for the duration of this call.
    /// </param>
    /// <param name="routing">
    /// When this method returns <see langword="true"/>, contains the routing metadata read from
    /// the envelope (<see cref="RequestEnvelopeContext.ResponseAddress"/>,
    /// <see cref="RequestEnvelopeContext.RequestId"/>, and optionally
    /// <see cref="RequestEnvelopeContext.FaultAddress"/>).
    /// When this method returns <see langword="false"/>, the value is <c>default</c>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the envelope was successfully parsed and contains at least a
    /// non-empty <c>requestId</c> field; <see langword="false"/> if the field is absent, the
    /// value is malformed, or the body cannot be parsed (e.g. malformed JSON).
    /// Implementations must never throw — format errors must be swallowed and reported as
    /// <see langword="false"/>.
    /// </returns>
    bool TryReadRequestEnvelope(ReadOnlySequence<byte> body, out RequestEnvelopeContext routing);
}
