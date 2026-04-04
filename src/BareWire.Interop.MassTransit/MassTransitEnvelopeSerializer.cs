using System.Buffers;
using System.Text.Json;

using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;
using BareWire.Serialization.Json;

namespace BareWire.Interop.MassTransit;

internal sealed class MassTransitEnvelopeSerializer : IMessageSerializer
{
    private static readonly JsonWriterOptions s_writerOptions = new() { SkipValidation = true };

    // Thread-local pooling per ADR-003 — one writer per thread, no sharing.
    [ThreadStatic]
    private static Utf8JsonWriter? t_writer;

    // Cache URN string per generic type — avoids string allocation per Serialize call.
    private static class UrnCache<T>
    {
        internal static readonly string Value = $"urn:message:{typeof(T).Namespace}:{typeof(T).Name}";
    }

    public string ContentType => "application/vnd.masstransit+json";

    public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(output);

        Utf8JsonWriter writer = t_writer ??= new Utf8JsonWriter(Stream.Null, s_writerOptions);
        writer.Reset(output);
        try
        {
            writer.WriteStartObject();
            writer.WriteString("messageId"u8, Guid.NewGuid());

            writer.WriteStartArray("messageType"u8);
            writer.WriteStringValue(UrnCache<T>.Value);
            writer.WriteEndArray();

            writer.WriteString("sentTime"u8, DateTimeOffset.UtcNow);

            writer.WritePropertyName("message"u8);
            JsonSerializer.Serialize(writer, message, BareWireJsonSerializerOptions.Default);

            writer.WriteEndObject();
            writer.Flush();
        }
        catch (JsonException ex)
        {
            throw new BareWireSerializationException(
                $"Failed to serialize {typeof(T).Name} to MassTransit envelope.",
                ContentType,
                targetType: typeof(T),
                rawPayload: null,
                innerException: ex);
        }
        finally
        {
            writer.Reset(Stream.Null);
        }
    }
}
