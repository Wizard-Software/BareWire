using System.Buffers;

namespace BareWire.Abstractions.Serialization;

/// <summary>
/// Optional extension of <see cref="IMessageSerializer"/> for serializers that can embed
/// per-request routing metadata into the message envelope.
/// </summary>
/// <remarks>
/// <para>
/// The core <see cref="IMessageSerializer"/> contract is intentionally stateless and does not carry
/// per-request information. Serializers that produce envelope formats supporting request/response
/// patterns (e.g. the MassTransit JSON envelope) may additionally implement this interface to accept
/// a <see cref="RequestEnvelopeContext"/> and write fields such as <c>responseAddress</c>,
/// <c>requestId</c>, <c>destinationAddress</c>, <c>faultAddress</c>, and <c>expirationTime</c>
/// into the envelope.
/// </para>
/// <para>
/// Implementing this interface is strictly opt-in. Raw serializers and format-agnostic serializers
/// that do not produce envelopes do not need to implement it; the transport will fall back to the
/// plain <see cref="IMessageSerializer.Serialize{T}"/> overload automatically. This design is
/// a non-breaking addition — no existing <see cref="IMessageSerializer"/> implementation is
/// affected.
/// </para>
/// <para>
/// <b>Thread safety:</b> Implementations must be safe to call from multiple threads concurrently.
/// The <see cref="RequestEnvelopeContext"/> parameter is passed by <c>in</c> reference to avoid
/// unnecessary copies; implementations must not capture or retain the reference beyond the call.
/// </para>
/// </remarks>
public interface IRequestEnvelopeSerializer
{
    /// <summary>
    /// Serializes <paramref name="message"/> together with the per-request routing metadata in
    /// <paramref name="context"/> into <paramref name="output"/> without intermediate allocations.
    /// </summary>
    /// <typeparam name="T">The message type to serialize. Must be a reference type.</typeparam>
    /// <param name="message">The message instance to serialize. Must not be null.</param>
    /// <param name="context">
    /// Per-request routing metadata (addresses, request identifier, expiration). Passed by
    /// <c>in</c> reference; do not retain a reference to it beyond the scope of this call.
    /// </param>
    /// <param name="output">The buffer writer to write the serialized bytes into. Must not be null.</param>
    void Serialize<T>(T message, in RequestEnvelopeContext context, IBufferWriter<byte> output)
        where T : class;
}
