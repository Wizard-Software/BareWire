using System.Globalization;
using BareWire.Abstractions;

namespace BareWire.CloudEvents;

/// <summary>
/// Provides extension methods on <see cref="ConsumeContext"/> for accessing CloudEvents 1.0
/// attributes encoded in the standard <c>ce-*</c> transport headers.
/// </summary>
/// <remarks>
/// Header values are sender-controlled and therefore constitute an external trust boundary.
/// All parsing in this class uses defensive <c>Try*</c> methods — no call site in this class
/// ever throws on malformed input. If parsing fails, the message is treated as a non-CloudEvent
/// and <see langword="null"/> is returned.
/// </remarks>
public static class CloudEventContextExtensions
{
    private const string HeaderId = "ce-id";
    private const string HeaderSource = "ce-source";
    private const string HeaderSpecVersion = "ce-specversion";
    private const string HeaderType = "ce-type";
    private const string HeaderSubject = "ce-subject";
    private const string HeaderTime = "ce-time";
    private const string HeaderDataContentType = "ce-datacontenttype";
    private const string HeaderDataSchema = "ce-dataschema";
    private const string HeaderPrefix = "ce-";

    /// <summary>
    /// Attempts to extract CloudEvents 1.0 context attributes from the <c>ce-*</c> transport headers
    /// of the consumed message.
    /// </summary>
    /// <param name="context">The consume context of the inbound message. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// An <see cref="ICloudEventAttributes"/> instance populated from the <c>ce-*</c> headers,
    /// or <see langword="null"/> when:
    /// <list type="bullet">
    ///   <item><description>Any of the four mandatory CE headers (<c>ce-id</c>, <c>ce-source</c>,
    ///   <c>ce-specversion</c>, <c>ce-type</c>) is absent.</description></item>
    ///   <item><description>A mandatory header is present but its value cannot be parsed
    ///   (e.g. <c>ce-source</c> is not a valid URI).</description></item>
    /// </list>
    /// This method never throws on malformed header values — the contract is "return null, never throw".
    /// Optional attributes that fail to parse are silently omitted (left <see langword="null"/>).
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    public static ICloudEventAttributes? GetCloudEvent(this ConsumeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyDictionary<string, string> headers = context.Headers;

        // All four mandatory headers must be present.
        if (!headers.TryGetValue(HeaderId, out string? id) ||
            !headers.TryGetValue(HeaderSource, out string? sourceRaw) ||
            !headers.TryGetValue(HeaderSpecVersion, out string? specVersion) ||
            !headers.TryGetValue(HeaderType, out string? type))
        {
            return null;
        }

        // Mandatory string attributes must not be null/empty (headers.TryGetValue may return null
        // if the value itself is null — treat that as absent).
        if (string.IsNullOrEmpty(id) ||
            string.IsNullOrEmpty(sourceRaw) ||
            string.IsNullOrEmpty(specVersion) ||
            string.IsNullOrEmpty(type))
        {
            return null;
        }

        // SEC-1: Parse mandatory URI using TryCreate — never new Uri(string) which throws.
        if (!Uri.TryCreate(sourceRaw, UriKind.RelativeOrAbsolute, out Uri? source))
        {
            return null;
        }

        // Optional: subject (plain string, no parsing required).
        headers.TryGetValue(HeaderSubject, out string? subject);

        // Optional: time (RFC 3339 / ISO 8601). SEC-1: TryParse only — never DateTimeOffset.Parse.
        DateTimeOffset? time = null;
        if (headers.TryGetValue(HeaderTime, out string? timeRaw) && !string.IsNullOrEmpty(timeRaw))
        {
            if (DateTimeOffset.TryParse(
                    timeRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsedTime))
            {
                time = parsedTime;
            }
            // If parsing fails, silently omit — optional attribute, does not invalidate the event.
        }

        // Optional: dataContentType (plain string, no parsing required).
        headers.TryGetValue(HeaderDataContentType, out string? dataContentType);

        // Optional: dataSchema URI. SEC-1: TryCreate only — never new Uri(string) which throws.
        Uri? dataSchema = null;
        if (headers.TryGetValue(HeaderDataSchema, out string? dataSchemaRaw) && !string.IsNullOrEmpty(dataSchemaRaw))
        {
            Uri.TryCreate(dataSchemaRaw, UriKind.RelativeOrAbsolute, out dataSchema);
            // If parsing fails, silently omit — optional attribute, does not invalidate the event.
        }

        // Collect extension attributes: any ce-* header not in the known set above.
        Dictionary<string, string>? extensions = null;
        foreach (KeyValuePair<string, string> header in headers)
        {
            if (!header.Key.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Skip the known standard attributes.
            if (string.Equals(header.Key, HeaderId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, HeaderSource, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, HeaderSpecVersion, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, HeaderType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, HeaderSubject, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, HeaderTime, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, HeaderDataContentType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, HeaderDataSchema, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            extensions ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            extensions[header.Key] = header.Value;
        }

        return new CloudEventContext(
            id: id,
            source: source,
            type: type,
            specVersion: specVersion,
            subject: subject,
            time: time,
            dataContentType: dataContentType,
            dataSchema: dataSchema,
            extensions: extensions);
    }
}
