using System.Buffers;
using System.Text.Json;

using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;
using BareWire.Serialization.Json;

namespace BareWire.Interop.MassTransit;

/// <summary>
/// Serializes messages wrapped in a MassTransit-compatible JSON envelope
/// (<c>application/vnd.masstransit+json</c>), enabling BareWire to publish messages
/// that MassTransit consumers can transparently consume.
/// </summary>
/// <remarks>
/// This class is <see langword="public"/> solely so that it can be referenced by name in
/// <c>IBusConfigurator.MapSerializer&lt;TMessage, MassTransitEnvelopeSerializer&gt;()</c>
/// for publish-only bridge scenarios where no receive endpoint is required.
/// The default bus behavior remains raw-first per ADR-001 — mapping a type to this serializer
/// is an explicit opt-in that does not affect other message types.
/// <para>
/// This class is stateless and thread-safe. Use <c>services.AddMassTransitEnvelopeSerializer()</c>
/// to register it in the DI container as a Singleton (recommended) before calling
/// <c>MapSerializer&lt;TMessage, MassTransitEnvelopeSerializer&gt;()</c>.
/// </para>
/// </remarks>
public sealed class MassTransitEnvelopeSerializer : IMessageSerializer
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
