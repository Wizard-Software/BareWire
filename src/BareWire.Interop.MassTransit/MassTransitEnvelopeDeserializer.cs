using System.Buffers;
using System.Text;
using System.Text.Json;

using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;
using BareWire.Serialization.Json;

namespace BareWire.Interop.MassTransit;

/// <summary>
/// Deserializes messages from the MassTransit JSON envelope format
/// (<c>application/vnd.masstransit+json</c>).
/// </summary>
/// <remarks>
/// <para>
/// This class additionally implements <see cref="IResponseEnvelopeReader"/>, enabling the
/// BareWire request client to extract the <c>requestId</c> from a response envelope for
/// fallback correlation when the transport-level correlation identifier is absent.
/// </para>
/// <para>
/// <see cref="TryReadRequestId"/> uses an incremental <see cref="Utf8JsonReader"/> scan and
/// never allocates the full <see cref="MassTransitEnvelope"/> record. It catches
/// <see cref="JsonException"/> internally and returns <see langword="false"/> — it never
/// throws (SEC-3).
/// </para>
/// </remarks>
internal sealed class MassTransitEnvelopeDeserializer : IMessageDeserializer, IResponseEnvelopeReader
{
    // Pre-encoded property name for fast comparison in the Utf8JsonReader scan (no string alloc).
    private static readonly byte[] s_requestIdPropertyName = "requestId"u8.ToArray();

    /// <inheritdoc/>
    public string ContentType => "application/vnd.masstransit+json";

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    /// <remarks>
    /// Uses an incremental <see cref="Utf8JsonReader"/> scan to locate the top-level
    /// <c>requestId</c> property without materializing the full envelope record.
    /// This avoids allocating <see cref="MassTransitEnvelope"/>, its
    /// <c>Headers</c> dictionary, and <c>MessageType</c> list — important because
    /// this method is called on the receive hot-path (SEC-3 / ADR-003).
    /// <para>
    /// This method never throws. Any <see cref="JsonException"/> is caught and results
    /// in a <see langword="false"/> return with <paramref name="requestId"/> set to
    /// <see cref="Guid.Empty"/>. No catch-all <c>catch (Exception)</c> is used,
    /// per CONSTITUTION §2.
    /// </para>
    /// </remarks>
    public bool TryReadRequestId(ReadOnlySequence<byte> body, out Guid requestId)
    {
        requestId = default;

        if (body.IsEmpty)
            return false;

        try
        {
            var reader = new Utf8JsonReader(body, isFinalBlock: true, state: default);

            // We expect the top-level token to be a JSON object.
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            // Scan top-level property names only (depth 0 after the opening brace).
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals(s_requestIdPropertyName))
                    {
                        // Advance to the property value.
                        if (!reader.Read())
                            return false;

                        if (reader.TokenType != JsonTokenType.String)
                            return false;

                        // Guid.TryParse handles the standard "D" format (8-4-4-4-12) emitted by
                        // Utf8JsonWriter.WriteString(ReadOnlySpan<byte>, Guid) as well as braced
                        // and no-hyphen variants that MassTransit may produce.
                        return Guid.TryParse(reader.GetString(), out requestId);
                    }
                    else
                    {
                        // Skip the value of any other top-level property (including nested objects/arrays).
                        reader.Skip();
                    }
                }
            }

            // requestId property was not present in the envelope.
            return false;
        }
        catch (JsonException)
        {
            requestId = default;
            return false;
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
