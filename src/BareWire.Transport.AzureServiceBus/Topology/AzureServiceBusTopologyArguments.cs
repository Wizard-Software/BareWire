using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;

namespace BareWire.Transport.AzureServiceBus.Topology;

/// <summary>
/// Parsed Azure Service Bus-specific queue parameters extracted from a
/// <see cref="QueueDeclaration"/>'s <c>Arguments</c> dictionary.
/// Used by <c>AzureServiceBusTransportAdapter.DeployTopologyAsync</c> to configure
/// the queue via <c>ServiceBusAdministrationClient.CreateQueueAsync</c>.
/// </summary>
internal readonly record struct AzureServiceBusQueueSpec(
    int MaxDeliveryCount,
    TimeSpan LockDuration,
    bool RequiresDuplicateDetection);

/// <summary>
/// Constant argument keys recognised by the Azure Service Bus transport adapter and the
/// parser logic for extracting queue parameters from <see cref="QueueDeclaration.Arguments"/>.
/// </summary>
internal static class AzureServiceBusTopologyArguments
{
    /// <summary>
    /// Argument key for the maximum delivery count before a message is dead-lettered.
    /// Value: <c>int</c>, default: <c>10</c> (Azure Service Bus default).
    /// </summary>
    internal const string MaxDeliveryCount = "bw.asb.max-delivery-count";

    /// <summary>
    /// Argument key for the message lock duration in ISO-8601 format (e.g. <c>PT30S</c>)
    /// or as a <see cref="TimeSpan"/>-parseable string.
    /// Value: <c>string</c> or <see cref="TimeSpan"/>, default: <c>PT30S</c> (30 seconds).
    /// </summary>
    internal const string LockDuration = "bw.asb.lock-duration";

    /// <summary>
    /// Argument key for enabling duplicate detection on the queue.
    /// Value: <c>bool</c> or <c>"true"</c>/<c>"false"</c>, default: <see langword="false"/>.
    /// When <see langword="true"/>, ASB uses the <c>MessageId</c> for duplicate detection
    /// within the duplicate-detection history window.
    /// </summary>
    internal const string RequiresDuplicateDetection = "bw.asb.requires-duplicate-detection";

    /// <summary>
    /// Parses <see cref="QueueDeclaration.Arguments"/> into an <see cref="AzureServiceBusQueueSpec"/>.
    /// When <paramref name="queue"/> has no <c>Arguments</c>, returns defaults
    /// (10 max-delivery-count, 30-second lock, no duplicate detection).
    /// </summary>
    /// <param name="queue">The queue declaration to parse.</param>
    /// <returns>An <see cref="AzureServiceBusQueueSpec"/> with the extracted or default values.</returns>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when <c>bw.asb.max-delivery-count</c> is not a valid positive integer, or
    /// <c>bw.asb.lock-duration</c> is not a parseable <see cref="TimeSpan"/> or ISO-8601 duration.
    /// </exception>
    internal static AzureServiceBusQueueSpec Parse(QueueDeclaration queue)
    {
        IReadOnlyDictionary<string, object>? args = queue.Arguments;

        if (args is null || args.Count == 0)
        {
            return DefaultSpec();
        }

        int maxDeliveryCount = 10;
        TimeSpan lockDuration = TimeSpan.FromSeconds(30);
        bool requiresDuplicateDetection = false;

        foreach ((string key, object value) in args)
        {
            if (key == MaxDeliveryCount)
            {
                maxDeliveryCount = ParseInt32(key, value);
                if (maxDeliveryCount < 1)
                {
                    throw new BareWireConfigurationException(
                        optionName: MaxDeliveryCount,
                        optionValue: maxDeliveryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        expectedValue: "An integer >= 1");
                }
            }
            else if (key == LockDuration)
            {
                lockDuration = ParseTimeSpan(key, value);
            }
            else if (key == RequiresDuplicateDetection)
            {
                requiresDuplicateDetection = ParseBool(key, value);
            }
            // Unknown BW argument keys are silently ignored (forward-compatible).
        }

        return new AzureServiceBusQueueSpec(maxDeliveryCount, lockDuration, requiresDuplicateDetection);
    }

    private static AzureServiceBusQueueSpec DefaultSpec() =>
        new(MaxDeliveryCount: 10, LockDuration: TimeSpan.FromSeconds(30), RequiresDuplicateDetection: false);

    private static int ParseInt32(string key, object value)
    {
        try
        {
            return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new BareWireConfigurationException(
                optionName: key,
                optionValue: value.ToString(),
                expectedValue: "A valid 32-bit integer",
                innerException: ex);
        }
    }

    private static TimeSpan ParseTimeSpan(string key, object value)
    {
        // Support TimeSpan directly or parseable string (e.g. "00:00:30" or "PT30S").
        if (value is TimeSpan ts)
        {
            return ts;
        }

        string? str = value.ToString();

        if (str is not null && TimeSpan.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan parsed))
        {
            return parsed;
        }

        throw new BareWireConfigurationException(
            optionName: key,
            optionValue: str,
            expectedValue: "A parseable TimeSpan string (e.g. '00:00:30') or TimeSpan value");
    }

    private static bool ParseBool(string key, object value)
    {
        if (value is bool b)
        {
            return b;
        }

        string? str = value.ToString();

        if (bool.TryParse(str, out bool parsed))
        {
            return parsed;
        }

        throw new BareWireConfigurationException(
            optionName: key,
            optionValue: str,
            expectedValue: "A boolean value ('true' or 'false')");
    }
}
