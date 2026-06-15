using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;

namespace BareWire.CloudEvents;

// ---------------------------------------------------------------------------
// Internal DTO — envelope shape for structured-mode CloudEvents 1.0 JSON.
// All nine JSON-bound fields carry explicit [JsonPropertyName] because
// CloudEventsJsonSerializerOptions.Default uses JsonSerializerDefaults.Web
// (camelCase), which would silently mis-map lowercase CE names such as
// "specversion" → "specVersion", "datacontenttype", "dataschema" (GAP-1).
// ---------------------------------------------------------------------------

internal sealed record CloudEventsEnvelope
{
    [JsonPropertyName("specversion")]
    public string? SpecVersion { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("source")]
    public Uri? Source { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("subject")]
    public string? Subject { get; init; }

    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; init; }

    [JsonPropertyName("datacontenttype")]
    public string? DataContentType { get; init; }

    [JsonPropertyName("dataschema")]
    public Uri? DataSchema { get; init; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

// ---------------------------------------------------------------------------
// Lightweight private adapter used solely to run CloudEventAttributeValidator
// without constructing a full CloudEventContext (which would throw on null
// mandatory fields before the validator can report a BareWireSerializationException).
// GAP-2: CloudEventContext is NOT built here; that belongs to 13.11.
// ---------------------------------------------------------------------------

file sealed class EnvelopeAttributeAdapter : ICloudEventAttributes
{
    private readonly CloudEventsEnvelope _envelope;

    internal EnvelopeAttributeAdapter(CloudEventsEnvelope envelope)
    {
        _envelope = envelope;
    }

    // The interface declares non-nullable Uri Source; we intentionally use null!
    // here so that a missing "source" in the envelope is detected by the validator
    // (which checks attributes.Source is null) rather than by a NullReferenceException.
    public Uri Source => _envelope.Source!;

    public string Id => _envelope.Id ?? string.Empty;

    public string SpecVersion => _envelope.SpecVersion ?? string.Empty;

    public string Type => _envelope.Type ?? string.Empty;

    public string? Subject => _envelope.Subject;

    public DateTimeOffset? Time => _envelope.Time;

    public string? DataContentType => _envelope.DataContentType;

    public Uri? DataSchema => _envelope.DataSchema;

    // PERF-2: extensions are NOT projected to IReadOnlyDictionary on the validate-only
    // path — the validator never reads Extensions. An empty dict avoids null-guard cost.
    public IReadOnlyDictionary<string, string> Extensions { get; } =
        new Dictionary<string, string>(0);
}

/// <summary>
/// Deserializes CloudEvents 1.0 structured-mode JSON envelopes
/// (<c>application/cloudevents+json</c>) from a <see cref="ReadOnlySequence{T}"/>
/// without intermediate byte-array allocations on the hot path (ADR-003).
/// </summary>
/// <remarks>
/// <para>
/// This deserializer is the symmetric counterpart of <see cref="CloudEventsEnvelopeSerializer"/>
/// (task 13.8). It reads the CE context attributes and the inline <c>data</c> JSON object,
/// validates mandatory attributes fail-fast via
/// <see cref="CloudEventAttributeValidator.ValidateMandatory"/>, then deserializes
/// <c>data</c> into the requested CLR type.
/// </para>
/// <para>
/// Any <see cref="JsonException"/> from the parse or payload deserialize step is wrapped in
/// a <see cref="BareWireSerializationException"/> that carries the content type, target type,
/// and a capped raw-payload excerpt for diagnostics.
/// </para>
/// <para>
/// SECURITY NOTE: Input size and extension-attribute-count limits are NOT enforced by this
/// class. DoS hardening is deferred to task 13.10 (SEC-1) and currently relies on transport-
/// frame limits imposed above this layer (e.g. RabbitMQ max-frame-size / max-message-size).
/// </para>
/// </remarks>
internal sealed class CloudEventsEnvelopeDeserializer : IMessageDeserializer
{
    /// <inheritdoc/>
    public string ContentType => CloudEventsEnvelopeContentType.Value;

    /// <summary>
    /// Deserializes a CloudEvents 1.0 structured-mode JSON envelope and returns the
    /// inner <c>data</c> payload as an instance of <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The CLR type to deserialize the <c>data</c> field into.</typeparam>
    /// <param name="data">
    /// The raw bytes of the <c>application/cloudevents+json</c> document.
    /// An empty sequence returns <see langword="null"/> (empty-payload contract).
    /// </param>
    /// <returns>
    /// The deserialized payload, or <see langword="null"/> if <paramref name="data"/> is empty
    /// or if the <c>data</c> field itself deserializes to <see langword="null"/>.
    /// </returns>
    /// <exception cref="BareWireSerializationException">
    /// Thrown when the envelope JSON is malformed, a mandatory CE attribute is absent/invalid,
    /// or the <c>data</c> field cannot be deserialized into <typeparamref name="T"/>.
    /// </exception>
    public T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class
    {
        if (data.IsEmpty)
            return null;

        var reader = new Utf8JsonReader(data);
        try
        {
            CloudEventsEnvelope? envelope = JsonSerializer.Deserialize<CloudEventsEnvelope>(
                ref reader, CloudEventsJsonSerializerOptions.Default);

            if (envelope is null)
                return null;

            // Validate mandatory CE 1.0 attributes fail-fast (13.3 / FR-5).
            // Uses a lightweight adapter so the validator — not CloudEventContext ctor —
            // reports the failure as BareWireSerializationException (GAP-2).
            var adapter = new EnvelopeAttributeAdapter(envelope);
            CloudEventAttributeValidator.ValidateMandatory(adapter, ContentType);

            return envelope.Data.Deserialize<T>(CloudEventsJsonSerializerOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new BareWireSerializationException(
                $"Failed to deserialize CloudEvents envelope for {typeof(T).Name}.",
                ContentType,
                targetType: typeof(T),
                rawPayload: ExtractRawPayload(data),
                innerException: ex);
        }
    }

    // ExtractRawPayload is called ONLY from the catch (JsonException) block — cold path.
    // The string allocation here is intentional and diagnostic-only (no hot-path impact).
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
