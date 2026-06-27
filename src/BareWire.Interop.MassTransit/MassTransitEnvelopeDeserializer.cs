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
/// <para>
/// <strong>Deserialization-hardening parity with the type-less path.</strong> The MassTransit
/// envelope is unauthenticated, producer-controlled foreign input. <see cref="Deserialize{T}"/>
/// shares <c>BareWireJsonSerializerOptions.Default</c> with the type-less
/// <c>SystemTextJsonRawDeserializer</c>, so its <em>security profile</em> is identical to type-less:
/// no polymorphic <c>TypeInfoResolver</c> is configured (a <c>$type</c> discriminator is treated as
/// an unknown property and ignored — no type-confusion gadget chain), and the default
/// <c>System.Text.Json</c> <c>MaxDepth</c> (64) is enforced. Depth is gated in the <em>stage-1</em>
/// envelope parse: materializing the <c>message</c> as a <see cref="System.Text.Json.JsonElement"/>
/// walks the whole subtree under the same depth counter, so the inner message is in fact
/// <em>~62-bounded</em> — slightly stricter than type-less. Payload size is bounded at the transport
/// boundary (bounded channels + broker max frame), identically to type-less; no deserializer-level
/// size cap is imposed here.
/// </para>
/// <para>
/// Parity refers to the <em>security profile</em>, not the per-message cost: unlike the single-pass
/// type-less path, the envelope path is <em>two-stage</em> — stage-1 materializes the <c>message</c>
/// as a <see cref="System.Text.Json.JsonElement"/> (a buffer), then stage-2 re-parses it into
/// the requested message type <c>T</c> (≈2× parse plus the envelope overhead). This deliberately differs from
/// the sibling <c>CloudEventsEnvelopeDeserializer</c>, which applies its own tighter limits
/// (<c>MaxDepth=32</c> + an explicit <c>CloudEventsEnvelopeLimits</c> size cap) under a separate
/// CloudEvents hardening regime. The MT path keeps parity-with-type-less by design; the stage-1
/// depth gate plus the transport size bound keep the vector bounded. The trust boundary assumes
/// broker-level publish ACLs are enforced; a consumer that combines <c>UseMassTransitEnvelope()</c>
/// with <c>AcceptUntyped()</c> additionally requires a schema-validation middleware (SEC-13), for
/// which a startup advisory is emitted by <c>UntypedTrustBoundaryDiagnostic</c>.
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
