using System.Text;
using BareWire.Abstractions.Exceptions;

namespace BareWire.Transport.Google.PubSub;

/// <summary>
/// Maps BareWire canonical headers to Google Cloud Pub/Sub <c>PubsubMessage.Attributes</c>
/// (<c>MapField&lt;string, string&gt;</c>) and vice versa.
/// </summary>
/// <remarks>
/// <para>
/// <b>Attribute limits (SEC-1 / SEC-4):</b> Pub/Sub allows at most 100 attributes per message,
/// with keys ≤ 256 UTF-8 bytes and values ≤ 1024 UTF-8 bytes.
/// <see cref="MapOutbound"/> validates all limits BEFORE the SDK call and throws
/// <see cref="BareWireTransportException"/> on violation. Exception messages contain only
/// counts and byte lengths — never key text or value text (SEC-4 anti-leak).
/// </para>
/// <para>
/// <b>Ordering key:</b> when the BareWire header <c>BW-OrderingKey</c> is present, it is
/// extracted and set on <c>PubsubMessage.OrderingKey</c> by the adapter. Full resolution
/// logic (priority ladder: <c>BW-OrderingKey</c> → <c>correlation-id</c> → empty) lives in
/// <c>PubSubOrderingKeyResolver</c> (implemented in R5.2).
/// </para>
/// </remarks>
internal sealed class PubSubHeaderMapper
{
    private const int MaxPubSubAttributes = 100;
    private const int MaxKeyBytes = 256;
    private const int MaxValueBytes = 1024;

    /// <summary>
    /// BareWire canonical header for the Pub/Sub ordering key.
    /// When present and non-empty, the value is passed through to
    /// <c>PubsubMessage.OrderingKey</c>. Full resolution logic (priority ladder including
    /// <c>correlation-id</c> fallback) is implemented in <c>PubSubOrderingKeyResolver</c> (R5.2).
    /// </summary>
    internal const string OrderingKeyHeader = "BW-OrderingKey";

    /// <summary>
    /// Copies BareWire headers to a <see cref="Dictionary{TKey,TValue}"/> of string pairs
    /// suitable for use as <c>PubsubMessage.Attributes</c>.
    /// </summary>
    /// <param name="bareWireHeaders">The BareWire headers from the outbound message. Must not be null.</param>
    /// <returns>
    /// A <see cref="Dictionary{TKey,TValue}"/> of attribute name → string value (Ordinal).
    /// Returns an empty dictionary when <paramref name="bareWireHeaders"/> is empty.
    /// </returns>
    /// <exception cref="BareWireTransportException">
    /// Thrown when the header count exceeds 100 (Pub/Sub hard limit) — message contains
    /// only the count (SEC-4).
    /// Thrown when any key exceeds 256 UTF-8 bytes or any value exceeds 1024 UTF-8 bytes —
    /// message contains only the attribute index and byte length, never the key or value text (SEC-4).
    /// </exception>
    internal static Dictionary<string, string> MapOutbound(
        IReadOnlyDictionary<string, string> bareWireHeaders)
    {
        ArgumentNullException.ThrowIfNull(bareWireHeaders);

        // SEC-4: check count before iterating — message contains ONLY the count.
        if (bareWireHeaders.Count > MaxPubSubAttributes)
        {
            throw new BareWireTransportException(
                message: $"Cannot send Pub/Sub message: {bareWireHeaders.Count} headers exceed the " +
                         $"Pub/Sub limit of {MaxPubSubAttributes} attributes per message.",
                transportName: "Google.PubSub",
                endpointAddress: null);
        }

        var result = new Dictionary<string, string>(bareWireHeaders.Count, StringComparer.Ordinal);

        int attributeIndex = 0;
        foreach (KeyValuePair<string, string> header in bareWireHeaders)
        {
            // SEC-1: validate key byte length BEFORE SDK call (key text not included in exception).
            int keyBytes = Encoding.UTF8.GetByteCount(header.Key);
            if (keyBytes > MaxKeyBytes)
            {
                // SEC-4: message contains only index and byte length — never the key text.
                throw new BareWireTransportException(
                    message: $"Cannot send Pub/Sub message: attribute at index {attributeIndex} has a key " +
                             $"of {keyBytes} UTF-8 bytes, which exceeds the Pub/Sub limit of {MaxKeyBytes} bytes.",
                    transportName: "Google.PubSub",
                    endpointAddress: null);
            }

            // SEC-1: validate value byte length BEFORE SDK call (value text not included in exception).
            int valueBytes = Encoding.UTF8.GetByteCount(header.Value);
            if (valueBytes > MaxValueBytes)
            {
                // SEC-4: message contains only index and byte length — never the value text.
                throw new BareWireTransportException(
                    message: $"Cannot send Pub/Sub message: attribute at index {attributeIndex} has a value " +
                             $"of {valueBytes} UTF-8 bytes, which exceeds the Pub/Sub limit of {MaxValueBytes} bytes.",
                    transportName: "Google.PubSub",
                    endpointAddress: null);
            }

            result[header.Key] = header.Value;
            attributeIndex++;
        }

        return result;
    }

    /// <summary>
    /// Maps the <c>Attributes</c> of a received <c>PubsubMessage</c> to a BareWire header dictionary.
    /// </summary>
    /// <param name="attributes">
    /// The Pub/Sub message attributes. May be <see langword="null"/> or empty.
    /// </param>
    /// <returns>
    /// A <see cref="Dictionary{TKey,TValue}"/> (Ordinal) of BareWire header name → string value.
    /// Returns an empty dictionary when <paramref name="attributes"/> is null or empty.
    /// </returns>
    internal static Dictionary<string, string> MapInbound(
        IDictionary<string, string>? attributes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (attributes is null)
        {
            return result;
        }

        foreach (KeyValuePair<string, string> attr in attributes)
        {
            result[attr.Key] = attr.Value;
        }

        return result;
    }

    /// <summary>
    /// Computes the attribute-inclusive byte size of a single outbound message for batch chunking
    /// (PERF-1 fix). The estimate accounts for body bytes, attribute key+value bytes (UTF-8),
    /// and ordering key bytes to avoid exceeding the 10 MB Pub/Sub publish request limit.
    /// </summary>
    /// <param name="bodyLength">The byte length of the message body.</param>
    /// <param name="headers">The outbound headers (mapped to Pub/Sub attributes).</param>
    /// <param name="orderingKey">The ordering key (empty string if not set).</param>
    /// <returns>The estimated total byte size of the message contribution to the request.</returns>
    internal static long EstimateMessageBytes(
        int bodyLength,
        IReadOnlyDictionary<string, string> headers,
        string orderingKey)
    {
        long size = bodyLength;

        foreach (KeyValuePair<string, string> header in headers)
        {
            size += Encoding.UTF8.GetByteCount(header.Key);
            size += Encoding.UTF8.GetByteCount(header.Value);
        }

        if (!string.IsNullOrEmpty(orderingKey))
        {
            size += Encoding.UTF8.GetByteCount(orderingKey);
        }

        return size;
    }
}
