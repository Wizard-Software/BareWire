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
/// This class additionally implements <see cref="IRequestEnvelopeSerializer"/>, which is used
/// by the BareWire request client to include routing metadata (<c>requestId</c>,
/// <c>responseAddress</c>, <c>destinationAddress</c>, <c>faultAddress</c>,
/// <c>expirationTime</c>) required for MassTransit request/response correlation.
/// When called via the plain <see cref="IMessageSerializer.Serialize{T}"/> overload (no
/// context), output is byte-identical to the previous behavior — routing fields are not emitted.
/// </para>
/// <para>
/// This class is stateless and thread-safe. Use <c>services.AddMassTransitEnvelopeSerializer()</c>
/// to register it in the DI container as a Singleton (recommended) before calling
/// <c>MapSerializer&lt;TMessage, MassTransitEnvelopeSerializer&gt;()</c>.
/// </para>
/// </remarks>
public sealed class MassTransitEnvelopeSerializer : IMessageSerializer, IRequestEnvelopeSerializer
{
    // Pre-encoded property name bytes used with typed WriteString overloads (zero .ToString() allocations).
    private static readonly byte[] s_requestIdName = "requestId"u8.ToArray();
    private static readonly byte[] s_responseAddressName = "responseAddress"u8.ToArray();
    private static readonly byte[] s_destinationAddressName = "destinationAddress"u8.ToArray();
    private static readonly byte[] s_faultAddressName = "faultAddress"u8.ToArray();
    private static readonly byte[] s_expirationTimeName = "expirationTime"u8.ToArray();
    private static readonly byte[] s_correlationIdName = "correlationId"u8.ToArray();

    private static readonly JsonWriterOptions s_writerOptions = new() { SkipValidation = true };

    // Thread-local pooling per ADR-003 — one writer per thread, no sharing.
    [ThreadStatic]
    private static Utf8JsonWriter? t_writer;

    // Cache URN string per generic type — avoids string allocation per Serialize call.
    private static class UrnCache<T>
    {
        internal static readonly string Value = $"urn:message:{typeof(T).Namespace}:{typeof(T).Name}";
    }

    /// <inheritdoc/>
    public string ContentType => "application/vnd.masstransit+json";

    /// <inheritdoc/>
    public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(output);

        RequestEnvelopeContext emptyContext = default;
        WriteEnvelope(message, in emptyContext, output);
    }

    /// <inheritdoc/>
    public void Serialize<T>(T message, in RequestEnvelopeContext context, IBufferWriter<byte> output)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(output);

        WriteEnvelope(message, in context, output);
    }

    /// <summary>
    /// Shared implementation that writes the MassTransit envelope.
    /// Routing fields are emitted only when the corresponding values are non-empty.
    /// When called from <see cref="Serialize{T}(T, IBufferWriter{byte})"/>, the default
    /// context has all routing fields null/empty, so no routing fields are written —
    /// preserving byte-identical output to the previous implementation.
    /// </summary>
    private static void WriteEnvelope<T>(T message, in RequestEnvelopeContext context, IBufferWriter<byte> output)
        where T : class
    {
        Utf8JsonWriter writer = t_writer ??= new Utf8JsonWriter(Stream.Null, s_writerOptions);
        writer.Reset(output);
        try
        {
            writer.WriteStartObject();
            writer.WriteString("messageId"u8, Guid.NewGuid());

            // ── Routing fields — only when present ─────────────────────────────
            if (context.RequestId != Guid.Empty)
                writer.WriteString(s_requestIdName, context.RequestId);

            if (!string.IsNullOrEmpty(context.ResponseAddress))
                writer.WriteString(s_responseAddressName, context.ResponseAddress);

            if (!string.IsNullOrEmpty(context.DestinationAddress))
                writer.WriteString(s_destinationAddressName, context.DestinationAddress);

            if (!string.IsNullOrEmpty(context.FaultAddress))
                writer.WriteString(s_faultAddressName, context.FaultAddress);

            if (context.ExpirationTime.HasValue)
                writer.WriteString(s_expirationTimeName, context.ExpirationTime.Value);

            if (context.CorrelationId.HasValue && context.CorrelationId.Value != Guid.Empty)
                writer.WriteString(s_correlationIdName, context.CorrelationId.Value);

            // ── Standard envelope fields ────────────────────────────────────────
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
                "application/vnd.masstransit+json",
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
