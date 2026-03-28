using System.Buffers;
using System.Text;
using System.Text.Json;

using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;

namespace BareWire.Serialization.Json;

internal sealed class BareWireEnvelopeSerializer : IMessageSerializer, IMessageDeserializer
{
    private static readonly JsonWriterOptions s_writerOptions = new() { SkipValidation = true };

    // Thread-local pooling — same rationale as SystemTextJsonSerializer.
    [ThreadStatic]
    private static Utf8JsonWriter? t_writer;

    public string ContentType => "application/vnd.barewire+json";

    public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(output);

        Utf8JsonWriter writer = t_writer ??= new Utf8JsonWriter(Stream.Null, s_writerOptions);
        writer.Reset(output);
        try
        {
            // Write envelope fields directly to avoid intermediate JsonElement allocation.
            // Property names use camelCase to match BareWireJsonSerializerOptions.Default.
            // Null values are omitted to match DefaultIgnoreCondition.WhenWritingNull.
            writer.WriteStartObject();
            writer.WriteString("messageId"u8, Guid.NewGuid());

            writer.WriteStartArray("messageType"u8);
            writer.WriteStringValue($"urn:message:{typeof(T).Namespace}:{typeof(T).Name}");
            writer.WriteEndArray();

            writer.WriteString("sentTime"u8, DateTimeOffset.UtcNow);

            writer.WritePropertyName("body"u8);
            JsonSerializer.Serialize(writer, message, BareWireJsonSerializerOptions.Default);

            writer.WriteEndObject();
            writer.Flush();
        }
        catch (JsonException ex)
        {
            throw new BareWireSerializationException(
                $"Failed to serialize envelope for {typeof(T).Name}.",
                ContentType,
                targetType: typeof(T),
                innerException: ex);
        }
        finally
        {
            writer.Reset(Stream.Null);
        }
    }

    public T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class
    {
        if (data.IsEmpty)
            return null;

        var reader = new Utf8JsonReader(data);
        try
        {
            BareWireEnvelope? envelope = JsonSerializer.Deserialize<BareWireEnvelope>(ref reader, BareWireJsonSerializerOptions.Default);

            if (envelope is null)
                return null;

            return envelope.Body.Deserialize<T>(BareWireJsonSerializerOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new BareWireSerializationException(
                $"Failed to deserialize envelope for {typeof(T).Name}.",
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
