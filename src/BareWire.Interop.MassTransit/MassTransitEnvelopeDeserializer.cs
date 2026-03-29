using System.Buffers;
using System.Text;
using System.Text.Json;

using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;
using BareWire.Serialization.Json;

namespace BareWire.Interop.MassTransit;

internal sealed class MassTransitEnvelopeDeserializer : IMessageDeserializer
{
    public string ContentType => "application/vnd.masstransit+json";

    public T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class
    {
        if (data.IsEmpty)
            return null;

        var reader = new Utf8JsonReader(data);
        try
        {
            MassTransitEnvelope? envelope = JsonSerializer.Deserialize<MassTransitEnvelope>(ref reader, BareWireJsonSerializerOptions.Default);

            if (envelope is null)
                return null;

            if (envelope.Message is null || envelope.Message.Value.ValueKind == JsonValueKind.Null)
                return null;

            return envelope.Message.Value.Deserialize<T>(BareWireJsonSerializerOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new BareWireSerializationException(
                $"Failed to deserialize MassTransit envelope for {typeof(T).Name}.",
                ContentType,
                targetType: typeof(T),
                rawPayload: ExtractRawPayload(data),
                innerException: ex);
        }
    }

    private static string? ExtractRawPayload(ReadOnlySequence<byte> data)
    {
        const int maxBytes = BareWireSerializationException.MaxRawPayloadLength;

        if (data.Length <= maxBytes)
        {
            return data.IsSingleSegment
                ? Encoding.UTF8.GetString(data.FirstSpan)
                : Encoding.UTF8.GetString(data);
        }

        ReadOnlySequence<byte> slice = data.Slice(0, maxBytes);
        return slice.IsSingleSegment
            ? Encoding.UTF8.GetString(slice.FirstSpan)
            : Encoding.UTF8.GetString(slice);
    }
}
