using System.Text;
using Confluent.Kafka;

namespace BareWire.Transport.Kafka;

/// <summary>
/// Maps BareWire canonical headers to Confluent.Kafka <see cref="Headers"/> and vice versa.
/// All header values are encoded/decoded as UTF-8 byte sequences.
/// </summary>
internal sealed class KafkaHeaderMapper
{
    /// <summary>
    /// Maps a BareWire header dictionary to a Confluent.Kafka <see cref="Headers"/> instance.
    /// All entries are encoded as UTF-8. Never returns <see langword="null"/>.
    /// </summary>
    /// <param name="bareWireHeaders">The BareWire headers from the outbound message.</param>
    /// <returns>
    /// A <see cref="Headers"/> instance containing one entry per input header key/value pair.
    /// Returns an empty <see cref="Headers"/> instance when <paramref name="bareWireHeaders"/> is empty.
    /// </returns>
    internal static Headers MapOutbound(IReadOnlyDictionary<string, string> bareWireHeaders)
    {
        ArgumentNullException.ThrowIfNull(bareWireHeaders);

        var headers = new Headers();

        foreach (KeyValuePair<string, string> header in bareWireHeaders)
        {
            headers.Add(header.Key, Encoding.UTF8.GetBytes(header.Value));
        }

        return headers;
    }

    /// <summary>
    /// Maps a Confluent.Kafka <see cref="Headers"/> instance to a BareWire header dictionary.
    /// All header values are decoded as UTF-8. Never returns <see langword="null"/>.
    /// </summary>
    /// <param name="kafkaHeaders">The Kafka message headers. May be <see langword="null"/>.</param>
    /// <returns>
    /// A dictionary of BareWire header name → value. Returns an empty dictionary when
    /// <paramref name="kafkaHeaders"/> is <see langword="null"/> or empty.
    /// </returns>
    internal static Dictionary<string, string> MapInbound(Headers? kafkaHeaders)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (kafkaHeaders is null)
        {
            return result;
        }

        foreach (IHeader header in kafkaHeaders)
        {
            result[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes());
        }

        return result;
    }
}
