using Azure.Messaging.ServiceBus;

namespace BareWire.Transport.AzureServiceBus;

/// <summary>
/// Maps BareWire canonical headers to Azure Service Bus <see cref="ServiceBusMessage"/>
/// <c>ApplicationProperties</c> and vice versa.
/// </summary>
internal sealed class AzureServiceBusHeaderMapper
{
    /// <summary>
    /// The BareWire header key used to carry the Azure Service Bus <c>SessionId</c> field
    /// on both the produce path (override via <c>BW-SessionId</c> header) and the consume path
    /// (stamped by <c>AzureServiceBusSessionConsumer</c> after <c>MapInbound</c>).
    /// </summary>
    /// <remarks>
    /// On the produce path the session resolver checks this header first, then falls back to
    /// <see cref="CorrelationIdHeader"/>. On the consume path the value is stamped after
    /// <c>MapInbound</c> so it cannot be spoofed from <c>ApplicationProperties</c>.
    /// </remarks>
    internal const string SessionIdHeader = "BW-SessionId";

    /// <summary>
    /// The canonical BareWire correlation-id header key (kebab-case, as populated by the bus).
    /// Used as a fallback source for <see cref="ServiceBusMessage.SessionId"/> on the produce path
    /// when <see cref="SessionIdHeader"/> is absent, enabling automatic per-saga-instance FIFO
    /// ordering without explicit header setting.
    /// </summary>
    /// <remarks>
    /// This key is kebab-case (<c>"correlation-id"</c>) matching how <c>BareWireBus</c> populates
    /// the header. The PascalCase form (<c>"CorrelationId"</c>) is a known pre-existing latent bug
    /// in <c>PartitionerMiddleware</c> and is out of scope for R2.2.
    /// </remarks>
    internal const string CorrelationIdHeader = "correlation-id";

    /// <summary>
    /// Copies all entries from <paramref name="bareWireHeaders"/> into the
    /// <see cref="ServiceBusMessage.ApplicationProperties"/> dictionary of the given
    /// <paramref name="message"/>. Never returns <see langword="null"/>.
    /// </summary>
    /// <param name="bareWireHeaders">The BareWire headers from the outbound message. Must not be null.</param>
    /// <param name="message">The <see cref="ServiceBusMessage"/> whose application properties to populate.</param>
    internal static void MapOutbound(IReadOnlyDictionary<string, string> bareWireHeaders, ServiceBusMessage message)
    {
        ArgumentNullException.ThrowIfNull(bareWireHeaders);
        ArgumentNullException.ThrowIfNull(message);

        foreach (KeyValuePair<string, string> header in bareWireHeaders)
        {
            message.ApplicationProperties[header.Key] = header.Value;
        }
    }

    /// <summary>
    /// Maps the <c>ApplicationProperties</c> of a received Service Bus message to a BareWire
    /// header dictionary. All values are converted to <see cref="string"/> via
    /// <see cref="Convert.ToString(object, System.IFormatProvider)"/>.
    /// Never returns <see langword="null"/>.
    /// </summary>
    /// <param name="applicationProperties">
    /// The application properties from a <see cref="ServiceBusReceivedMessage"/>.
    /// May be <see langword="null"/> or empty.
    /// </param>
    /// <returns>
    /// A <see cref="Dictionary{TKey,TValue}"/> (Ordinal) of BareWire header name → string value.
    /// Returns an empty dictionary when <paramref name="applicationProperties"/> is null or empty.
    /// </returns>
    internal static Dictionary<string, string> MapInbound(
        IReadOnlyDictionary<string, object>? applicationProperties)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (applicationProperties is null)
        {
            return result;
        }

        foreach (KeyValuePair<string, object> property in applicationProperties)
        {
            string value = Convert.ToString(property.Value, System.Globalization.CultureInfo.InvariantCulture)
                           ?? string.Empty;
            result[property.Key] = value;
        }

        return result;
    }
}
