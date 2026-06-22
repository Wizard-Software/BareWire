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
internal sealed class MassTransitEnvelopeDeserializer : IMessageDeserializer, IResponseEnvelopeReader, IRequestEnvelopeRouteReader
{
    // Pre-encoded property name bytes for fast ValueTextEquals comparison (no string alloc).
    private static readonly byte[] s_requestIdPropertyName = "requestId"u8.ToArray();
    private static readonly byte[] s_responseAddressPropertyName = "responseAddress"u8.ToArray();
    private static readonly byte[] s_faultAddressPropertyName = "faultAddress"u8.ToArray();

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

                        // PERF-1 / ASSM-1: TryGetGuid is zero-alloc (no intermediate string).
                        // Consistent with TryReadRequestEnvelope which already uses this API.
                        return reader.TryGetGuid(out requestId);
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

    /// <inheritdoc/>
    /// <remarks>
    /// Uses an incremental <see cref="Utf8JsonReader"/> scan to locate the top-level
    /// <c>requestId</c>, <c>responseAddress</c>, and <c>faultAddress</c> properties without
    /// materializing the full <see cref="MassTransitEnvelope"/> record.
    /// <para>
    /// This method never throws. Any <see cref="JsonException"/> is caught and results in a
    /// <see langword="false"/> return. Returns <see langword="false"/> when <c>requestId</c> is
    /// absent — <c>responseAddress</c> alone is insufficient without a correlation id (SEC-1).
    /// </para>
    /// <para>
    /// Uses <see cref="Utf8JsonReader.TryGetGuid"/> for <c>requestId</c> extraction to avoid
    /// the string allocation that <c>Guid.TryParse(reader.GetString())</c> would incur (PERF-1).
    /// </para>
    /// </remarks>
    public bool TryReadRequestEnvelope(ReadOnlySequence<byte> body, out RequestEnvelopeContext routing)
    {
        routing = default;

        if (body.IsEmpty)
            return false;

        try
        {
            var reader = new Utf8JsonReader(body, isFinalBlock: true, state: default);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            Guid requestId = Guid.Empty;
            string? responseAddress = null;
            string? faultAddress = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (reader.ValueTextEquals(s_requestIdPropertyName))
                {
                    if (!reader.Read())
                        return false;

                    if (reader.TokenType != JsonTokenType.String)
                        return false;

                    // PERF-1: TryGetGuid is zero-alloc (no intermediate string).
                    if (!reader.TryGetGuid(out requestId))
                        return false;
                }
                else if (reader.ValueTextEquals(s_responseAddressPropertyName))
                {
                    if (!reader.Read())
                        return false;

                    if (reader.TokenType == JsonTokenType.String)
                        responseAddress = reader.GetString();
                }
                else if (reader.ValueTextEquals(s_faultAddressPropertyName))
                {
                    if (!reader.Read())
                        return false;

                    if (reader.TokenType == JsonTokenType.String)
                        faultAddress = reader.GetString();
                }
                else
                {
                    // Skip any other top-level property value (including nested objects/arrays).
                    reader.Skip();
                }
            }

            if (requestId == Guid.Empty)
                return false;

            routing = new RequestEnvelopeContext(
                ResponseAddress: responseAddress,
                DestinationAddress: null,
                FaultAddress: faultAddress,
                RequestId: requestId,
                CorrelationId: null,
                ExpirationTime: null);

            return true;
        }
        catch (JsonException)
        {
            routing = default;
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
