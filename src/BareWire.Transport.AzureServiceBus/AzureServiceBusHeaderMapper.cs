using Azure.Messaging.ServiceBus;

namespace BareWire.Transport.AzureServiceBus;

/// <summary>
/// Maps BareWire canonical headers to Azure Service Bus <see cref="ServiceBusMessage"/>
/// <c>ApplicationProperties</c> and vice versa.
/// </summary>
internal sealed class AzureServiceBusHeaderMapper
{
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
