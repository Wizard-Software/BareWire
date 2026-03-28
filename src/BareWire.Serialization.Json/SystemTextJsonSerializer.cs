using System.Buffers;
using System.Text.Json;

using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;

namespace BareWire.Serialization.Json;

internal sealed class SystemTextJsonSerializer : IMessageSerializer
{
    private static readonly JsonWriterOptions s_writerOptions = new() { SkipValidation = true };

    // Thread-local pooling of Utf8JsonWriter to avoid ~448 B allocation per Serialize call.
    // Utf8JsonWriter.Reset() reuses internal buffers across calls on the same thread.
    // This is thread-local state (not shared mutable state) — safe per ADR-003.
    [ThreadStatic]
    private static Utf8JsonWriter? t_writer;

    public string ContentType => "application/json";

    public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(output);

        Utf8JsonWriter writer = t_writer ??= new Utf8JsonWriter(Stream.Null, s_writerOptions);
        writer.Reset(output);
        try
        {
            JsonSerializer.Serialize(writer, message, BareWireJsonSerializerOptions.Default);
            writer.Flush();
        }
        catch (JsonException ex)
        {
            throw new BareWireSerializationException(
                $"Failed to serialize {typeof(T).Name}.",
                ContentType,
                targetType: typeof(T),
                innerException: ex);
        }
        finally
        {
            writer.Reset(Stream.Null);
        }
    }
}
