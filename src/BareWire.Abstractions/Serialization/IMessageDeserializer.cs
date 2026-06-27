using System.Buffers;

namespace BareWire.Abstractions.Serialization;

/// <summary>
/// Deserializes raw byte sequences into typed messages following the raw-first contract (no envelope by default).
/// Implementations read directly from a <see cref="ReadOnlySequence{T}"/> to achieve zero-copy deserialization
/// without intermediate byte array allocations.
/// </summary>
public interface IMessageDeserializer
{
    /// <summary>
    /// Gets the MIME content type that this deserializer handles (e.g. <c>application/json</c>).
    /// The pipeline matches inbound messages to deserializers using this value.
    /// </summary>
    string ContentType { get; }

    /// <summary>
    /// Deserializes the byte sequence in <paramref name="data"/> into an instance of <typeparamref name="T"/>.
    /// Returns <see langword="null"/> if the data cannot be deserialized into the requested type.
    /// </summary>
    /// <typeparam name="T">The target message type. Must be a reference type.</typeparam>
    /// <param name="data">The raw zero-copy byte sequence to deserialize.</param>
    /// <returns>The deserialized instance, or <see langword="null"/> on failure.</returns>
    T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class;
}
