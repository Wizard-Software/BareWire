using System.Text;
using RabbitMQ.Client;

namespace BareWire.Transport.RabbitMQ;

/// <summary>
/// Maps AMQP message properties and headers to BareWire canonical headers, and vice versa.
/// Default AMQP ↔ BareWire mappings are always applied; a <see cref="RabbitMqHeaderMappingConfigurator"/>
/// can override or extend them.
/// </summary>
/// <remarks>
/// <para>
/// Headers with the reserved <c>BW-</c> prefix are transport-authoritative: they must originate either
/// from this transport itself or from an AMQP property this mapper maps by default, never from an
/// arbitrary raw header a publisher put on the wire.
/// </para>
/// <para>
/// On outbound, an unmapped <c>BW-</c>-prefixed header is never sent on the wire — the prefix is
/// compared case-insensitively (<see cref="StringComparison.OrdinalIgnoreCase"/>), so <c>bw-forged</c>
/// and <c>Bw-Forged</c> are stripped just like <c>BW-Forged</c>.
/// </para>
/// <para>
/// On inbound, an unmapped raw <c>BW-</c>-prefixed header is dropped (case-insensitive) without being
/// logged, with two exceptions compared using an exact, case-sensitive match: the mapping-epoch header
/// this transport itself stamps directly onto the AMQP headers dictionary, and <c>BW-MessageType</c>
/// when the result does not already carry a message type — that is, when the AMQP <c>Type</c> property
/// (or a configured <c>MapMessageType</c> source) left it empty. The <c>Type</c> property always wins
/// over the raw header when both are present.
/// </para>
/// <para>
/// An explicit header mapping configured via <see cref="RabbitMqHeaderMappingConfigurator.MapHeader"/> —
/// including an identity mapping such as mapping <c>BW-TenantId</c> to itself — takes precedence over
/// this filter in both directions, and is the supported way to carry a custom <c>BW-</c>-prefixed header
/// through the broker.
/// </para>
/// </remarks>
internal sealed class RabbitMqHeaderMapper
{
    // Reserved BareWire header prefix — see the trust-boundary remarks on the type above.
    private const string ReservedHeaderPrefix = "BW-";

    // Default AMQP property names used as source/destination keys in the custom-mapping lookup.
    private const string BwMessageId = "message-id";
    private const string BwCorrelationId = "correlation-id";
    private const string BwContentType = "content-type";
    private const string BwMessageType = "BW-MessageType";
    private const string BwTraceparent = "traceparent";

    private readonly RabbitMqHeaderMappingConfigurator? _config;

    internal RabbitMqHeaderMapper(RabbitMqHeaderMappingConfigurator? config = null)
    {
        _config = config;
    }

    /// <summary>
    /// Maps an inbound AMQP delivery's properties and headers to a BareWire header dictionary.
    /// </summary>
    /// <param name="properties">The AMQP properties from the delivery.</param>
    /// <returns>A dictionary of BareWire canonical header name → value.</returns>
    internal Dictionary<string, string> MapInbound(IReadOnlyBasicProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        // --- Standard AMQP properties → BareWire headers ---

        // MessageId
        if (!string.IsNullOrEmpty(properties.MessageId))
        {
            result[BwMessageId] = properties.MessageId;
        }

        // CorrelationId — may be overridden by a custom header mapping
        string correlationIdSource = _config?.CorrelationIdMapping ?? string.Empty;
        if (string.IsNullOrEmpty(correlationIdSource))
        {
            // Default: read from AMQP CorrelationId property
            if (!string.IsNullOrEmpty(properties.CorrelationId))
            {
                result[BwCorrelationId] = properties.CorrelationId;
            }
        }
        else
        {
            // Custom: read from AMQP Headers dictionary under the configured key
            string? customValue = ReadStringHeader(properties.Headers, correlationIdSource);
            if (customValue is not null)
            {
                result[BwCorrelationId] = customValue;
            }
        }

        // ContentType
        if (!string.IsNullOrEmpty(properties.ContentType))
        {
            result[BwContentType] = properties.ContentType;
        }

        // ReplyTo — response queue name for request-response pattern
        if (!string.IsNullOrEmpty(properties.ReplyTo))
        {
            result["ReplyTo"] = properties.ReplyTo;
        }

        // MessageType (Type property) — may be overridden by a custom header mapping
        string messageTypeSource = _config?.MessageTypeMapping ?? string.Empty;
        if (string.IsNullOrEmpty(messageTypeSource))
        {
            // Default: read from AMQP Type property
            if (!string.IsNullOrEmpty(properties.Type))
            {
                result[BwMessageType] = properties.Type;
            }
        }
        else
        {
            // Custom: read from AMQP Headers dictionary under the configured key
            string? customValue = ReadStringHeader(properties.Headers, messageTypeSource);
            if (customValue is not null)
            {
                result[BwMessageType] = customValue;
            }
        }

        // --- AMQP Headers dictionary ---
        if (properties.Headers is not null && properties.Headers.Count > 0)
        {
            bool ignoreUnmapped = _config?.ShouldIgnoreUnmapped ?? false;
            IReadOnlyDictionary<string, string>? customMappings = _config?.CustomMappings;

            foreach (KeyValuePair<string, object?> entry in properties.Headers)
            {
                if (entry.Value is null)
                {
                    continue;
                }

                string rawKey = entry.Key;

                // traceparent is always mapped 1:1 by default
                if (rawKey.Equals(BwTraceparent, StringComparison.OrdinalIgnoreCase))
                {
                    result[BwTraceparent] = ConvertHeaderValue(entry.Value);
                    continue;
                }

                // Check if there is a reverse custom mapping (transport header → BareWire header)
                // Custom mappings are stored as bareWire → transport, so we need reverse lookup.
                string? bareWireKey = FindBareWireKeyForTransportHeader(customMappings, rawKey);

                if (bareWireKey is not null)
                {
                    result[bareWireKey] = ConvertHeaderValue(entry.Value);
                }
                else if (IsReservedHeader(rawKey) && !IsAcceptedReservedWireHeader(rawKey, result))
                {
                    // Unmapped reserved BareWire header supplied by the publisher, not stamped by this
                    // transport — drop it so it cannot masquerade as transport-authoritative metadata.
                    // The value is intentionally never decoded here: dropped headers must not pay the
                    // conversion cost.
                }
                else if (!ignoreUnmapped)
                {
                    // Passthrough: no mapping defined but we are not filtering
                    result[rawKey] = ConvertHeaderValue(entry.Value);
                }
                // else: ignoreUnmapped=true and no mapping found → drop header
            }
        }

        return result;
    }

    /// <summary>
    /// Maps BareWire canonical headers to AMQP <see cref="BasicProperties"/> and a headers dictionary.
    /// </summary>
    /// <param name="bareWireHeaders">The BareWire headers from the outbound message.</param>
    /// <returns>
    /// A tuple of the populated <see cref="BasicProperties"/> and the AMQP headers dictionary
    /// (for non-property headers).
    /// </returns>
    internal (BasicProperties Props, Dictionary<string, object?> Headers) MapOutbound(
        IReadOnlyDictionary<string, string> bareWireHeaders)
    {
        ArgumentNullException.ThrowIfNull(bareWireHeaders);

        var props = new BasicProperties();
        var amqpHeaders = new Dictionary<string, object?>(StringComparer.Ordinal);

        bool ignoreUnmapped = _config?.ShouldIgnoreUnmapped ?? false;
        IReadOnlyDictionary<string, string>? customMappings = _config?.CustomMappings;

        // Resolve effective source key for correlation-id (may be custom)
        string correlationIdTransport = _config?.CorrelationIdMapping ?? string.Empty;
        // Resolve effective source key for message-type (may be custom)
        string messageTypeTransport = _config?.MessageTypeMapping ?? string.Empty;

        foreach (KeyValuePair<string, string> header in bareWireHeaders)
        {
            string bwKey = header.Key;
            string value = header.Value;

            // Map well-known BareWire headers to AMQP properties first
            if (bwKey.Equals(BwMessageId, StringComparison.Ordinal))
            {
                props.MessageId = value;
                continue;
            }

            if (bwKey.Equals(BwCorrelationId, StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(correlationIdTransport))
                {
                    // Default: AMQP CorrelationId property
                    props.CorrelationId = value;
                }
                else
                {
                    // Custom: put into headers under the configured transport key
                    amqpHeaders[correlationIdTransport] = value;
                }
                continue;
            }

            if (bwKey.Equals(BwContentType, StringComparison.Ordinal))
            {
                props.ContentType = value;
                continue;
            }

            if (bwKey.Equals(BwMessageType, StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(messageTypeTransport))
                {
                    // Default: AMQP Type property
                    props.Type = value;
                }
                else
                {
                    // Custom: put into headers under the configured transport key
                    amqpHeaders[messageTypeTransport] = value;
                }
                continue;
            }

            if (bwKey.Equals("ReplyTo", StringComparison.Ordinal))
            {
                props.ReplyTo = value;
                continue;
            }

            // Check explicit custom mappings (BareWire → transport header name).
            // This must happen BEFORE the BW- skip guard so that user-defined BW-* headers
            // with an explicit mapping are still forwarded under their transport name.
            if (customMappings is not null && customMappings.TryGetValue(bwKey, out string? transportKey))
            {
                amqpHeaders[transportKey] = value;
                continue;
            }

            // Skip remaining reserved BareWire headers (case-insensitive) — only the transport stamps
            // them on the wire.
            if (IsReservedHeader(bwKey))
            {
                continue;
            }

            // No explicit mapping and not an internal BareWire header
            if (!ignoreUnmapped)
            {
                // Passthrough: include as-is in AMQP headers dictionary
                amqpHeaders[bwKey] = value;
            }
            // else: ignoreUnmapped=true → drop
        }

        return (props, amqpHeaders);
    }

    // Returns whether the key carries the reserved BareWire prefix, compared case-insensitively.
    private static bool IsReservedHeader(string key) =>
        key.StartsWith(ReservedHeaderPrefix, StringComparison.OrdinalIgnoreCase);

    // Returns whether an unmapped reserved header is nonetheless accepted from the raw AMQP headers
    // dictionary: either the mapping-epoch header this transport stamps itself (bypassing MapOutbound),
    // or BW-MessageType when the result does not already carry a message type (the AMQP Type property,
    // mapped earlier, always wins). Both comparisons are exact-case — a letter-case variant is not
    // accepted and falls through to the drop branch.
    private static bool IsAcceptedReservedWireHeader(string key, Dictionary<string, string> result) =>
        key.Equals(RabbitMqTransportAdapter.MappingEpochHeaderName, StringComparison.Ordinal) ||
        (key.Equals(BwMessageType, StringComparison.Ordinal) && !result.ContainsKey(BwMessageType));

    private static string? ReadStringHeader(IDictionary<string, object?>? headers, string key)
    {
        if (headers is null)
        {
            return null;
        }

        if (!headers.TryGetValue(key, out object? raw) || raw is null)
        {
            return null;
        }

        return ConvertHeaderValue(raw);
    }

    private static string ConvertHeaderValue(object value) =>
        value switch
        {
            string s => s,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => value.ToString() ?? string.Empty,
        };

    private static string? FindBareWireKeyForTransportHeader(
        IReadOnlyDictionary<string, string>? customMappings,
        string transportKey)
    {
        if (customMappings is null)
        {
            return null;
        }

        // customMappings is bareWire → transport; we need transport → bareWire (reverse lookup)
        foreach (KeyValuePair<string, string> mapping in customMappings)
        {
            if (mapping.Value.Equals(transportKey, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.Key;
            }
        }

        return null;
    }
}
