using System.Buffers;

using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;

using MessagePack;

namespace BareWire.Serialization.MsgPack;

/// <summary>
/// Zero-copy MessagePack serializer for BareWire.
/// Writes directly to an <see cref="IBufferWriter{T}"/> via <see cref="MessagePackWriter"/>
/// without allocating an intermediate <c>byte[]</c> (ADR-003).
/// </summary>
internal sealed class MessagePackSerializer : IMessageSerializer
{
    /// <inheritdoc />
    public string ContentType => "application/x-msgpack";

    /// <inheritdoc />
    public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            var writer = new MessagePackWriter(output);
            global::MessagePack.MessagePackSerializer.Serialize(ref writer, message, BareWireMessagePackSerializerOptions.Default);
            writer.Flush();
        }
        catch (MessagePackSerializationException ex)
        {
            throw new BareWireSerializationException(
                $"Failed to serialize {typeof(T).Name}.",
                ContentType,
                targetType: typeof(T),
                innerException: ex);
        }
    }
}
