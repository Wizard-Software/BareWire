using BareWire.Abstractions.Exceptions;

namespace BareWire.CloudEvents;

/// <summary>
/// Fail-fast validator for the four mandatory CloudEvents 1.0 context attributes
/// (<c>id</c>, <c>source</c>, <c>specversion</c>, <c>type</c>) and the CE 1.0 domain
/// rules that apply to them. Invoked by the binary binding (13.4) and the envelope
/// (de)serializers (13.8/13.9) before publish / after deserialize.
/// </summary>
internal static class CloudEventAttributeValidator
{
    /// <summary>The only CloudEvents specification version this implementation supports.</summary>
    internal const string SupportedSpecVersion = "1.0";

    // SEC-1: an echoed attribute value may originate from untrusted deserialization and is unbounded
    // until the 13.10 size hardening lands. BareWireSerializationException truncates only RawPayload,
    // never the Message, so any echoed value is capped to this length before interpolation.
    private const int MaxEchoedAttributeLength = 32;

    /// <summary>
    /// Validates the four mandatory CloudEvents 1.0 attributes, throwing fail-fast on the first violation.
    /// </summary>
    /// <param name="attributes">The CloudEvent attributes to validate. Must not be <see langword="null"/>.</param>
    /// <param name="contentType">
    /// The content type to attach to the thrown exception (e.g. <c>application/cloudevents+json</c>
    /// for structured mode, or the binary-mode content type for 13.4).
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="attributes"/> is <see langword="null"/>.</exception>
    /// <exception cref="BareWireSerializationException">
    /// Thrown when a mandatory attribute is missing/empty, or when <c>specversion</c> is unsupported.
    /// </exception>
    internal static void ValidateMandatory(ICloudEventAttributes attributes, string contentType)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        if (string.IsNullOrWhiteSpace(attributes.Id))
        {
            throw Fail("CloudEvents mandatory attribute 'id' is missing or empty.", contentType);
        }

        if (attributes.Source is null)
        {
            throw Fail("CloudEvents mandatory attribute 'source' is missing.", contentType);
        }

        if (string.IsNullOrWhiteSpace(attributes.Type))
        {
            throw Fail("CloudEvents mandatory attribute 'type' is missing or empty.", contentType);
        }

        if (string.IsNullOrWhiteSpace(attributes.SpecVersion))
        {
            throw Fail("CloudEvents mandatory attribute 'specversion' is missing or empty.", contentType);
        }

        if (!string.Equals(attributes.SpecVersion, SupportedSpecVersion, StringComparison.Ordinal))
        {
            throw Fail(
                $"Unsupported CloudEvents specversion '{Sanitize(attributes.SpecVersion)}'. "
                    + $"Only '{SupportedSpecVersion}' is supported.",
                contentType);
        }
    }

    private static BareWireSerializationException Fail(string message, string contentType)
        => new(message, contentType);

    // SEC-1: cap length (DoS amplification) and replace control/CR/LF chars (log-injection)
    // before an untrusted attribute value is echoed into an exception message.
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string capped = value.Length <= MaxEchoedAttributeLength
            ? value
            : string.Concat(value.AsSpan(0, MaxEchoedAttributeLength), "…");

        return string.Create(capped.Length, capped, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
            {
                span[i] = char.IsControl(src[i]) ? '?' : src[i];
            }
        });
    }
}
