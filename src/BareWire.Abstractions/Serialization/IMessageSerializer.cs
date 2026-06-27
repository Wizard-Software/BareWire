using System.Buffers;

namespace BareWire.Abstractions.Serialization;

/// <summary>
/// Serializes typed messages into a raw byte representation following the raw-first contract (no envelope by default).
/// Implementations write directly into an <see cref="IBufferWriter{T}"/> to achieve zero-copy serialization
/// without intermediate byte array allocations.
/// </summary>
public interface IMessageSerializer
{
    /// <summary>
    /// Gets the MIME content type produced by this serializer (e.g. <c>application/json</c>).
    /// This value is propagated as the <c>content-type</c> transport header.
    /// </summary>
    string ContentType { get; }

    /// <summary>
    /// Serializes <paramref name="message"/> into <paramref name="output"/> without intermediate allocations.
    /// </summary>
    /// <typeparam name="T">The message type to serialize. Must be a reference type.</typeparam>
    /// <param name="message">The message instance to serialize. Must not be null.</param>
    /// <param name="output">The buffer writer to write the serialized bytes into. Must not be null.</param>
    void Serialize<T>(T message, IBufferWriter<byte> output) where T : class;
}
