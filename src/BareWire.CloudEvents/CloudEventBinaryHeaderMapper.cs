using System.Globalization;

namespace BareWire.CloudEvents;

/// <summary>
/// Binary content-mode binding (ADR-007 §3): maps CloudEvents 1.0 context attributes
/// to/from transport headers carrying the HTTP-style <c>ce-</c> prefix. The payload (data) is
/// carried raw (no envelope), per ADR-001. Transport-neutral — operates on a plain
/// header dictionary; the RabbitMQ realization (<c>BasicProperties.Headers</c>, AMQP 0-9-1)
/// belongs to the activation API (13.6), NOT a certified AMQP 1.0 binding (R1, ADR-007 §R1).
/// </summary>
internal static class CloudEventBinaryHeaderMapper
{
    internal const string HeaderPrefix = "ce-";
    internal const string HeaderId = "ce-id";
    internal const string HeaderSource = "ce-source";
    internal const string HeaderSpecVersion = "ce-specversion";
    internal const string HeaderType = "ce-type";
    internal const string HeaderSubject = "ce-subject";
    internal const string HeaderTime = "ce-time";
    internal const string HeaderDataContentType = "ce-datacontenttype";
    internal const string HeaderDataSchema = "ce-dataschema";

    /// <summary>
    /// Writes the CE attributes into a fresh case-insensitive header dictionary.
    /// Mandatory attributes are always present; optional attributes are omitted when <see langword="null"/>
    /// or empty. Does NOT wrap or touch the payload (data) — binary mode carries data raw (ADR-001).
    /// Extension keys are emitted verbatim; their names must carry the <c>ce-</c> prefix per
    /// <see cref="ICloudEventAttributes"/> contract.
    /// </summary>
    /// <param name="attributes">The CloudEvents context attributes to serialize. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// A new <see cref="IDictionary{TKey,TValue}"/> with <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// containing the <c>ce-*</c> header entries. All keys start with the <c>ce-</c> prefix.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="attributes"/> is <see langword="null"/>.
    /// </exception>
    internal static IDictionary<string, string> ToHeaders(ICloudEventAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        // D6: wstępny rozmiar słownika eliminuje garbage z resize bucket-array;
        // 8 slotów obejmuje 4 obowiązkowe + 4 opcjonalne standardowe bez resize.
        var headers = new Dictionary<string, string>(8, StringComparer.OrdinalIgnoreCase)
        {
            [HeaderId] = attributes.Id,
            [HeaderSource] = attributes.Source.OriginalString,
            [HeaderSpecVersion] = attributes.SpecVersion,
            [HeaderType] = attributes.Type,
        };

        if (!string.IsNullOrEmpty(attributes.Subject))
        {
            headers[HeaderSubject] = attributes.Subject;
        }

        if (attributes.Time is { } time)
        {
            // D1/D5: formatowanie bez pośredniego string z ToString("O").
            // stackalloc char[35] — format "O" dla DateTimeOffset ma maks. 33 znaki
            // (np. "9999-12-31T23:59:59.9999999+00:00"), margines 2 znaków wyklucza cichy
            // fallback przy wartościach granicznych (D5 z planu 13.5).
            Span<char> buffer = stackalloc char[35];
            if (time.TryFormat(buffer, out int written, "O", CultureInfo.InvariantCulture))
            {
                headers[HeaderTime] = new string(buffer[..written]);
            }
            else
            {
                // Ścieżka awaryjna — TryFormat("O") nie powinna zwrócić false dla żadnej
                // prawidłowej wartości DateTimeOffset, ale zachowujemy fallback jako defensive programming.
                headers[HeaderTime] = time.ToString("O", CultureInfo.InvariantCulture);
            }
        }

        if (!string.IsNullOrEmpty(attributes.DataContentType))
        {
            headers[HeaderDataContentType] = attributes.DataContentType;
        }

        if (attributes.DataSchema is { } dataSchema)
        {
            headers[HeaderDataSchema] = dataSchema.OriginalString;
        }

        // D6: guard przed pętlą extensions eliminuje alokację zboxowanego enumeratora
        // interfejsu IReadOnlyDictionary (~32-40 B) w ścieżce mandatory-only (brak extensions).
        // Extension keys are already ce-* per ICloudEventAttributes contract — emit verbatim.
        if (attributes.Extensions.Count > 0)
        {
            foreach (KeyValuePair<string, string> kv in attributes.Extensions)
            {
                headers[kv.Key] = kv.Value;
            }
        }

        return headers;
    }

    /// <summary>
    /// Reads CE attributes from a <c>ce-*</c> header dictionary. Returns <see langword="false"/> (out
    /// <see langword="null"/>) when a mandatory attribute is absent or un-parseable — never throws
    /// (mirrors <c>GetCloudEvent</c> SEC-1 contract). Optional attributes that fail to parse are silently
    /// omitted. Extension attributes are lazily collected (no allocation when no extensions are present).
    /// </summary>
    /// <param name="headers">The inbound header dictionary. Must not be <see langword="null"/>.</param>
    /// <param name="attributes">
    /// When this method returns <see langword="true"/>, contains the parsed <see cref="ICloudEventAttributes"/>.
    /// When this method returns <see langword="false"/>, this is <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when all four mandatory CE attributes were successfully parsed;
    /// <see langword="false"/> otherwise.
    /// </returns>
    internal static bool TryFromHeaders(
        IReadOnlyDictionary<string, string> headers,
        out ICloudEventAttributes? attributes)
    {
        attributes = null;

        // All four mandatory headers must be present.
        if (!headers.TryGetValue(HeaderId, out string? id) ||
            !headers.TryGetValue(HeaderSource, out string? sourceRaw) ||
            !headers.TryGetValue(HeaderSpecVersion, out string? specVersion) ||
            !headers.TryGetValue(HeaderType, out string? type))
        {
            return false;
        }

        // Mandatory string attributes must not be null/empty.
        if (string.IsNullOrEmpty(id) ||
            string.IsNullOrEmpty(sourceRaw) ||
            string.IsNullOrEmpty(specVersion) ||
            string.IsNullOrEmpty(type))
        {
            return false;
        }

        // SEC-1: Parse mandatory URI using TryCreate — never new Uri(string) which throws.
        if (!Uri.TryCreate(sourceRaw, UriKind.RelativeOrAbsolute, out Uri? source))
        {
            return false;
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

        // Collect extension attributes: any ce-* header not in the known standard set.
        // Lazy allocation: no Dictionary allocated when no extensions are present (< 512 B/op budget).
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

        attributes = new CloudEventContext(
            id: id,
            source: source,
            type: type,
            specVersion: specVersion,
            subject: subject,
            time: time,
            dataContentType: dataContentType,
            dataSchema: dataSchema,
            extensions: extensions);

        return true;
    }
}
