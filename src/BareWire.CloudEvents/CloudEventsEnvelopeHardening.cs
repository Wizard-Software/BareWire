using System.Buffers;
using System.Text.Json;

using BareWire.Abstractions.Exceptions;

namespace BareWire.CloudEvents;

/// <summary>
/// Fail-fast pre-scan validator for CloudEvents 1.0 structured-mode envelopes.
/// Enforces bounded input limits before any costly DTO deserialization (SEC-1 / ADR-003).
/// </summary>
/// <remarks>
/// The pre-scan uses a <see cref="Utf8JsonReader"/> directly on the raw
/// <see cref="ReadOnlySequence{T}"/> (zero-copy, ADR-003) and never materializes
/// the payload or extension-data dictionary. Allocations are bounded to a lazily-created
/// <see cref="HashSet{T}"/> for extension-name duplicate detection.
/// </remarks>
internal static class CloudEventsEnvelopeHardening
{
    // SEC-1: cap echo'd names in exception messages (mirrors CloudEventAttributeValidator).
    private const int MaxEchoedAttributeLength = 32;

    // Bit positions for the 9 standard CE 1.0 context attributes (+ data).
    // Used to detect duplicates with zero heap allocation on the happy path.
    private const int BitSpecVersion      = 1 << 0;
    private const int BitId               = 1 << 1;
    private const int BitSource           = 1 << 2;
    private const int BitType             = 1 << 3;
    private const int BitSubject          = 1 << 4;
    private const int BitTime             = 1 << 5;
    private const int BitDataContentType  = 1 << 6;
    private const int BitDataSchema       = 1 << 7;
    private const int BitData             = 1 << 8;

    /// <summary>
    /// Validates a raw CloudEvents structured-mode JSON envelope against the provided limits,
    /// throwing <see cref="BareWireSerializationException"/> fail-fast on the first violation.
    /// </summary>
    /// <param name="data">The raw envelope bytes. Must not be empty (caller's responsibility).</param>
    /// <param name="limits">Configured hardening limits. Must not be <see langword="null"/>.</param>
    /// <param name="contentType">The MIME content type to attach to any thrown exception.</param>
    /// <exception cref="BareWireSerializationException">
    /// Thrown when any hardening rule is violated (size, attribute count, attribute value size,
    /// extension name validity, or duplicate context attribute).
    /// </exception>
    internal static void Validate(
        ReadOnlySequence<byte> data,
        CloudEventsEnvelopeLimits limits,
        string contentType)
    {
        // Rule 1 — SEC-1 (size): fail-fast before constructing Utf8JsonReader.
        if (data.Length > limits.MaxEnvelopeSizeBytes)
        {
            throw Fail("Envelope size exceeds limit.", contentType);
        }

        var reader = new Utf8JsonReader(data);

        // Advance to the first token. If the envelope is not a JSON object, the next
        // read in the deserializer's JsonSerializer.Deserialize will throw JsonException,
        // which the existing catch block in the deserializer handles uniformly.
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            // Not a JSON object at top level — let JsonException propagate from deserializer.
            return;
        }

        int attributeCount = 0;
        int seenStandardBits = 0;
        HashSet<string>? seenExtensionNames = null;

        // Walk depth-1 property names.
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            // Rule 2: attribute count limit.
            attributeCount++;
            if (attributeCount > limits.MaxAttributeCount)
            {
                throw Fail("Attribute count exceeds limit.", contentType);
            }

            // Identify standard vs. extension attribute via zero-alloc span comparison (PERF-2).
            int bit = GetStandardAttributeBit(ref reader);
            bool isStandard = bit != 0;
            bool isDataAttribute = bit == BitData;

            if (isStandard)
            {
                // Rule 5: duplicate standard attribute detection via bitmask (zero alloc).
                if ((seenStandardBits & bit) != 0)
                {
                    throw Fail("Duplicate context attribute detected.", contentType);
                }
                seenStandardBits |= bit;
            }
            else
            {
                // Extension attribute: validate name on raw UTF-8 bytes (no string alloc).
                ReadOnlySpan<byte> nameSpan = reader.ValueSpan;

                // Rule 4 + SEC-3: validate extension name charset and length.
                if (nameSpan.Length == 0 || nameSpan.Length > limits.MaxExtensionNameLength)
                {
                    throw Fail(
                        $"Invalid extension attribute name '{SanitizeSpan(nameSpan)}'.",
                        contentType);
                }

                foreach (byte b in nameSpan)
                {
                    bool isLowerAlpha = b >= (byte)'a' && b <= (byte)'z';
                    bool isDigit      = b >= (byte)'0' && b <= (byte)'9';
                    if (!isLowerAlpha && !isDigit)
                    {
                        throw Fail(
                            $"Invalid extension attribute name '{SanitizeSpan(nameSpan)}'.",
                            contentType);
                    }
                }

                // Duplicate extension detection (lazily-allocated HashSet).
                // String alloc only on first extension attribute encountered.
                string nameStr = System.Text.Encoding.UTF8.GetString(nameSpan);
                seenExtensionNames ??= new HashSet<string>(StringComparer.Ordinal);
                if (!seenExtensionNames.Add(nameStr))
                {
                    throw Fail($"Duplicate context attribute '{Sanitize(nameStr)}'.", contentType);
                }
            }

            // Advance to the value token.
            if (!reader.Read())
                break;

            // Rule 3: scalar attribute value size (String/Number tokens).
            // The 'data' attribute is exempt — its total size is bounded by Rule 1.
            // Extension attributes with non-scalar values (SEC-2) are handled below.
            if (isDataAttribute)
            {
                // Skip the entire data subtree — no value-length check here.
                reader.Skip();
            }
            else if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number)
            {
                if (reader.ValueSpan.Length > limits.MaxAttributeValueLength)
                {
                    throw Fail("Attribute value exceeds limit.", contentType);
                }
            }
            else if (!isStandard &&
                     reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                // SEC-2: non-scalar extension value — count consumed bytes of the subtree
                // and reject if the subtree exceeds MaxAttributeValueLength.
                long startIndex = reader.TokenStartIndex;
                reader.Skip();
                long consumed = reader.BytesConsumed - startIndex;
                if (consumed > limits.MaxAttributeValueLength)
                {
                    throw Fail("Attribute value exceeds limit.", contentType);
                }
            }
            // Standard non-scalar values (e.g. data already handled above) — no check needed.
        }
    }

    // Returns the bitmask bit for standard CE 1.0 attribute names using zero-alloc
    // span comparison (Ordinal, case-sensitive per CE 1.0 spec).
    // Returns 0 if the name is not a standard CE attribute.
    private static int GetStandardAttributeBit(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("specversion"u8))     return BitSpecVersion;
        if (reader.ValueTextEquals("id"u8))              return BitId;
        if (reader.ValueTextEquals("source"u8))          return BitSource;
        if (reader.ValueTextEquals("type"u8))            return BitType;
        if (reader.ValueTextEquals("subject"u8))         return BitSubject;
        if (reader.ValueTextEquals("time"u8))            return BitTime;
        if (reader.ValueTextEquals("datacontenttype"u8)) return BitDataContentType;
        if (reader.ValueTextEquals("dataschema"u8))      return BitDataSchema;
        if (reader.ValueTextEquals("data"u8))            return BitData;
        return 0;
    }

    private static BareWireSerializationException Fail(string message, string contentType)
        => new(message, contentType);

    // Mirrors CloudEventAttributeValidator.Sanitize: cap at MaxEchoedAttributeLength,
    // replace control characters with '?' to prevent log-injection.
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        string capped = value.Length <= MaxEchoedAttributeLength
            ? value
            : string.Concat(value.AsSpan(0, MaxEchoedAttributeLength), "…");

        return string.Create(capped.Length, capped, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
                span[i] = char.IsControl(src[i]) ? '?' : src[i];
        });
    }

    // Sanitizes a raw UTF-8 span for echo in exception messages.
    // Decodes to string first (bounded by MaxEchoedAttributeLength bytes before decode).
    private static string SanitizeSpan(ReadOnlySpan<byte> nameSpan)
    {
        // Cap at MaxEchoedAttributeLength bytes before decoding to avoid large alloc.
        ReadOnlySpan<byte> capped = nameSpan.Length <= MaxEchoedAttributeLength
            ? nameSpan
            : nameSpan[..MaxEchoedAttributeLength];

        return Sanitize(System.Text.Encoding.UTF8.GetString(capped));
    }
}
