using System.Buffers;

using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;

using MessagePack;

namespace BareWire.Serialization.MsgPack;

/// <summary>
/// Zero-copy MessagePack deserializer for BareWire.
/// Reads directly from a <see cref="ReadOnlySequence{T}"/> (including multi-segment sequences)
/// via <see cref="MessagePackReader"/> without copying bytes into a contiguous buffer (ADR-003).
/// </summary>
internal sealed class MessagePackDeserializer : IMessageDeserializer
{
    /// <inheritdoc />
    public string ContentType => "application/x-msgpack";

    /// <inheritdoc />
    public T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class
    {
        if (data.IsEmpty)
            return null;

        try
        {
            var reader = new MessagePackReader(data);
            return global::MessagePack.MessagePackSerializer.Deserialize<T>(ref reader, BareWireMessagePackSerializerOptions.Default);
        }
        catch (MessagePackSerializationException ex)
        {
            // rawPayload is intentionally omitted: binary MessagePack data is untrusted and must
            // not be embedded in exceptions or logs (SEC decision — avoids secret leakage).
            throw new BareWireSerializationException(
                $"Failed to deserialize {typeof(T).Name} from MessagePack.",
                ContentType,
                targetType: typeof(T),
                innerException: ex);
        }
    }
}
