using System.Buffers;
using System.Text.Json;

using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;

namespace BareWire.CloudEvents;

/// <summary>
/// Serializes messages as CloudEvents 1.0 structured-mode JSON envelopes
/// (<c>application/cloudevents+json</c>), writing CE context attributes and the
/// <c>data</c> payload as a single JSON document directly to an
/// <see cref="IBufferWriter{T}"/> without intermediate byte-array allocations (ADR-003).
/// </summary>
/// <remarks>
/// <para>
/// This serializer implements the CloudEvents 1.0 HTTP Content Modes — Structured Content Mode.
/// CE context attributes are written as top-level lowercase JSON properties; the event payload
/// is written inline as a nested JSON object under the <c>data</c> key.
/// </para>
/// <para>
/// Mandatory attributes (<c>id</c>, <c>source</c>, <c>specversion</c>, <c>type</c>) are validated
/// fail-fast by <see cref="CloudEventAttributeValidator.ValidateMandatory"/> before any write occurs,
/// ensuring no partial output is produced for invalid attribute sets.
/// </para>
/// <para>
/// Thread-safety: instances are safe to use concurrently. The underlying
/// <see cref="Utf8JsonWriter"/> is pooled via <c>[ThreadStatic]</c> and reset to
/// <see cref="Stream.Null"/> in the <c>finally</c> block to avoid holding a reference to the
/// caller's buffer across a call boundary.
/// </para>
/// <para>
/// Extension attribute name collisions with standard CE attributes (R3) are deferred to
/// task 13.10 (SEC-1 hardening). In this implementation extension keys are written verbatim
/// and assumed to be disjoint from standard attribute names.
/// </para>
/// </remarks>
internal sealed class CloudEventsEnvelopeSerializer : IMessageSerializer
{
    private static readonly JsonWriterOptions s_writerOptions = new() { SkipValidation = true };

    // Thread-local pooling — same rationale as BareWireEnvelopeSerializer.
    [ThreadStatic]
    private static Utf8JsonWriter? t_writer;

    private readonly ICloudEventAttributes _attributes;

    /// <summary>
    /// Initializes a new instance of <see cref="CloudEventsEnvelopeSerializer"/>.
    /// </summary>
    /// <param name="attributes">
    /// The CloudEvents context attributes to embed in every serialized envelope.
    /// Must not be <see langword="null"/>. Mandatory attributes are validated at serialize time.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="attributes"/> is <see langword="null"/>.
    /// </exception>
    internal CloudEventsEnvelopeSerializer(ICloudEventAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        _attributes = attributes;
    }

    /// <inheritdoc/>
    public string ContentType => CloudEventsEnvelopeContentType.Value;

    /// <inheritdoc/>
    public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(output);

        // Fail-fast: validate mandatory CE attributes BEFORE writing anything (13.3).
        CloudEventAttributeValidator.ValidateMandatory(_attributes, ContentType);

        Utf8JsonWriter writer = t_writer ??= new Utf8JsonWriter(Stream.Null, s_writerOptions);
        writer.Reset(output);
        try
        {
            writer.WriteStartObject();

            // Mandatory CE 1.0 attributes — always present (validated above).
            writer.WriteString("specversion"u8, _attributes.SpecVersion);
            writer.WriteString("id"u8, _attributes.Id);
            writer.WriteString("source"u8, _attributes.Source.OriginalString);
            writer.WriteString("type"u8, _attributes.Type);

            // Optional CE 1.0 attributes — omitted when null/empty.
            if (!string.IsNullOrEmpty(_attributes.Subject))
                writer.WriteString("subject"u8, _attributes.Subject);

            if (_attributes.Time is { } time)
                writer.WriteString("time"u8, time); // ISO 8601 / RFC3339 via BCL overload

            if (!string.IsNullOrEmpty(_attributes.DataContentType))
                writer.WriteString("datacontenttype"u8, _attributes.DataContentType);

            if (_attributes.DataSchema is { } schema)
                writer.WriteString("dataschema"u8, schema.OriginalString);

            // Extension attributes written verbatim (key collision deferred to 13.10 / SEC-1).
            // PERF-2: guard skips enumerator allocation when Extensions is empty (common case).
            if (_attributes.Extensions.Count > 0)
            {
                foreach (KeyValuePair<string, string> kv in _attributes.Extensions)
                    writer.WriteString(kv.Key, kv.Value);
            }

            // Inline JSON data payload — zero-copy write directly to the same writer.
            writer.WritePropertyName("data"u8);
            JsonSerializer.Serialize(writer, message, CloudEventsJsonSerializerOptions.Default);

            writer.WriteEndObject();
            writer.Flush();
        }
        catch (JsonException ex)
        {
            throw new BareWireSerializationException(
                $"Failed to serialize CloudEvents envelope for {typeof(T).Name}.",
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
