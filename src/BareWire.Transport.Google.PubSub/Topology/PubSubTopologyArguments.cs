using System.Globalization;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;

namespace BareWire.Transport.Google.PubSub.Topology;

/// <summary>
/// Parsed Google Cloud Pub/Sub-specific resource parameters extracted from a
/// <see cref="QueueDeclaration"/>'s <c>Arguments</c> dictionary.
/// Used by <c>PubSubTransportAdapter.DeployTopologyAsync</c> to configure subscriptions.
/// </summary>
internal readonly record struct PubSubResourceSpec(
    TimeSpan AckDeadline,
    bool OrderingEnabled,
    string? DeadLetterTopic,
    int MaxDeliveryAttempts,
    int MaxOutstandingMessages,
    long MaxOutstandingBytes);

/// <summary>
/// Constant argument keys recognised by the Google Cloud Pub/Sub transport adapter and the
/// parser logic for extracting resource parameters from <see cref="QueueDeclaration.Arguments"/>.
/// </summary>
internal static class PubSubTopologyArguments
{
    /// <summary>
    /// Argument key for the subscription's acknowledgement deadline.
    /// Value: <see cref="TimeSpan"/> or parseable string (e.g. <c>"00:01:00"</c>).
    /// Default: 60 seconds. Valid range: 10 – 600 seconds (Pub/Sub enforced).
    /// </summary>
    internal const string AckDeadlineKey = "bw.pubsub.ack-deadline";

    /// <summary>
    /// Argument key for enabling message ordering on the subscription.
    /// Value: <c>bool</c> or <c>"true"</c>/<c>"false"</c>. Default: <see langword="false"/>.
    /// </summary>
    internal const string OrderingEnabledKey = "bw.pubsub.ordering-enabled";

    /// <summary>
    /// Argument key for the dead-letter topic name to route messages to after
    /// <see cref="MaxDeliveryAttemptsKey"/> is exhausted. Full DLQ wiring in R5.3.
    /// Value: non-empty topic name. Default: <see langword="null"/> (no dead-letter policy).
    /// </summary>
    internal const string DeadLetterTopicKey = "bw.pubsub.dead-letter-topic";

    /// <summary>
    /// Argument key for the maximum delivery attempts before a message is sent to the
    /// dead-letter topic (<see cref="DeadLetterTopicKey"/>). Full DLQ wiring in R5.3.
    /// Value: <c>int</c>, range 5 – 100 (Pub/Sub enforced). Default: 5.
    /// </summary>
    internal const string MaxDeliveryAttemptsKey = "bw.pubsub.max-delivery-attempts";

    /// <summary>
    /// Argument key for the maximum number of outstanding (unacknowledged) messages
    /// allowed for this subscription's consumer. Used to configure per-subscription flow control.
    /// Value: <c>int</c>, must be &gt;= 1. Default: 1000.
    /// </summary>
    internal const string MaxOutstandingMessagesKey = "bw.pubsub.max-outstanding-messages";

    /// <summary>
    /// Argument key for the maximum total byte size of outstanding messages for this subscription.
    /// Value: <c>long</c>, must be &gt; 0. Default: 67,108,864 (64 MiB).
    /// </summary>
    internal const string MaxOutstandingBytesKey = "bw.pubsub.max-outstanding-bytes";

    /// <summary>
    /// Parses <see cref="QueueDeclaration.Arguments"/> into a <see cref="PubSubResourceSpec"/>.
    /// When <paramref name="queue"/> has no <c>Arguments</c>, returns defaults.
    /// Unknown <c>bw.pubsub.*</c> keys are silently ignored (forward-compatible).
    /// </summary>
    /// <param name="queue">The queue declaration to parse.</param>
    /// <returns>A <see cref="PubSubResourceSpec"/> with the extracted or default values.</returns>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when a known key has a value that cannot be parsed or is out of the valid range.
    /// </exception>
    internal static PubSubResourceSpec Parse(QueueDeclaration queue)
    {
        IReadOnlyDictionary<string, object>? args = queue.Arguments;

        if (args is null || args.Count == 0)
        {
            return DefaultSpec();
        }

        TimeSpan ackDeadline = TimeSpan.FromSeconds(60);
        bool orderingEnabled = false;
        string? deadLetterTopic = null;
        int maxDeliveryAttempts = 5;
        int maxOutstandingMessages = 1_000;
        long maxOutstandingBytes = 64L * 1024 * 1024;

        foreach ((string key, object value) in args)
        {
            if (key == AckDeadlineKey)
            {
                ackDeadline = ParseTimeSpanInRange(key, value, min: 10, max: 600);
            }
            else if (key == OrderingEnabledKey)
            {
                orderingEnabled = ParseBool(key, value);
            }
            else if (key == DeadLetterTopicKey)
            {
                deadLetterTopic = value.ToString();
            }
            else if (key == MaxDeliveryAttemptsKey)
            {
                maxDeliveryAttempts = ParseInt32InRange(key, value, min: 5, max: 100,
                    expectedDescription: "An integer in the range 5–100 (Pub/Sub max delivery attempts)");
            }
            else if (key == MaxOutstandingMessagesKey)
            {
                maxOutstandingMessages = ParseInt32InRange(key, value, min: 1, max: int.MaxValue,
                    expectedDescription: "An integer >= 1");
            }
            else if (key == MaxOutstandingBytesKey)
            {
                maxOutstandingBytes = ParseInt64Positive(key, value);
            }
            // Unknown bw.pubsub.* keys are silently ignored (forward-compatible).
        }

        return new PubSubResourceSpec(
            ackDeadline, orderingEnabled, deadLetterTopic, maxDeliveryAttempts,
            maxOutstandingMessages, maxOutstandingBytes);
    }

    private static PubSubResourceSpec DefaultSpec() =>
        new(
            AckDeadline: TimeSpan.FromSeconds(60),
            OrderingEnabled: false,
            DeadLetterTopic: null,
            MaxDeliveryAttempts: 5,
            MaxOutstandingMessages: 1_000,
            MaxOutstandingBytes: 64L * 1024 * 1024);

    private static TimeSpan ParseTimeSpanInRange(string key, object value, int min, int max)
    {
        TimeSpan ts;

        if (value is TimeSpan directTs)
        {
            ts = directTs;
        }
        else
        {
            string? str = value.ToString();
            if (str is null || !TimeSpan.TryParse(str, CultureInfo.InvariantCulture, out ts))
            {
                throw new BareWireConfigurationException(
                    optionName: key,
                    optionValue: str,
                    expectedValue: "A parseable TimeSpan string (e.g. '00:01:00') or TimeSpan value");
            }
        }

        double seconds = ts.TotalSeconds;
        if (seconds < min || seconds > max)
        {
            throw new BareWireConfigurationException(
                optionName: key,
                optionValue: seconds.ToString(CultureInfo.InvariantCulture),
                expectedValue: $"A TimeSpan between {min} and {max} seconds");
        }

        return ts;
    }

    private static int ParseInt32InRange(string key, object value, int min, int max, string expectedDescription)
    {
        int parsed;
        try
        {
            parsed = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new BareWireConfigurationException(
                optionName: key,
                optionValue: value.ToString(),
                expectedValue: expectedDescription,
                innerException: ex);
        }

        if (parsed < min || (max != int.MaxValue && parsed > max))
        {
            throw new BareWireConfigurationException(
                optionName: key,
                optionValue: parsed.ToString(CultureInfo.InvariantCulture),
                expectedValue: expectedDescription);
        }

        return parsed;
    }

    private static long ParseInt64Positive(string key, object value)
    {
        long parsed;
        try
        {
            parsed = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new BareWireConfigurationException(
                optionName: key,
                optionValue: value.ToString(),
                expectedValue: "A positive integer (long)",
                innerException: ex);
        }

        if (parsed <= 0)
        {
            throw new BareWireConfigurationException(
                optionName: key,
                optionValue: parsed.ToString(CultureInfo.InvariantCulture),
                expectedValue: "A positive integer (long) > 0");
        }

        return parsed;
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
