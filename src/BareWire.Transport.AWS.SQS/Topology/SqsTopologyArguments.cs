using System.Globalization;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;

namespace BareWire.Transport.AWS.SQS.Topology;

/// <summary>
/// Parsed Amazon SQS-specific queue parameters extracted from a <see cref="QueueDeclaration"/>'s
/// <c>Arguments</c> dictionary. Used by <c>SqsTransportAdapter.DeployTopologyAsync</c> to
/// configure queues via <c>IAmazonSQS.CreateQueueAsync</c>.
/// </summary>
internal readonly record struct SqsQueueSpec(
    TimeSpan VisibilityTimeout,
    int WaitTimeSeconds,
    bool IsFifo,
    int MaxReceiveCount,
    bool ContentBasedDeduplication);

/// <summary>
/// Constant argument keys recognised by the Amazon SQS transport adapter and the
/// parser logic for extracting queue parameters from <see cref="QueueDeclaration.Arguments"/>.
/// </summary>
internal static class SqsTopologyArguments
{
    /// <summary>
    /// Argument key for the queue's default visibility timeout.
    /// Value: <see cref="TimeSpan"/> or parseable string (e.g. <c>"00:00:30"</c>).
    /// Default: 30 seconds.
    /// </summary>
    internal const string VisibilityTimeout = "bw.sqs.visibility-timeout";

    /// <summary>
    /// Argument key for the queue's <c>ReceiveMessageWaitTimeSeconds</c> attribute.
    /// Value: <c>int</c>, range 0–20. Default: 20 (maximum long-polling).
    /// </summary>
    internal const string WaitTimeSecondsKey = "bw.sqs.wait-time-seconds";

    /// <summary>
    /// Argument key for enabling FIFO queue mode (R4.2).
    /// Value: <c>bool</c> or <c>"true"</c>/<c>"false"</c>. Default: <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, the queue name <b>must</b> end with <c>.fifo</c> — SQS enforces
    /// this at creation time. Full BareWire FIFO support (MessageGroupId / MessageDeduplicationId)
    /// is introduced in R4.2.
    /// </remarks>
    internal const string FifoKey = "bw.sqs.fifo";

    /// <summary>
    /// Argument key for the <c>maxReceiveCount</c> in the <c>RedrivePolicy</c>
    /// (how many times a message is delivered before being moved to the DLQ).
    /// Value: <c>int</c>, must be &gt;= 1. Default: 5.
    /// </summary>
    internal const string MaxReceiveCountKey = "bw.sqs.max-receive-count";

    /// <summary>
    /// Argument key for enabling content-based deduplication on FIFO queues (R4.2).
    /// Value: <c>bool</c> or <c>"true"</c>/<c>"false"</c>. Default: <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, SQS computes <c>MessageDeduplicationId</c> from a SHA-256
    /// hash of the message body — no explicit dedup id is required. Only valid for FIFO queues
    /// (SQS enforces this at the queue level). Set in combination with
    /// <c>ISqsConfigurator.ContentBasedDeduplication()</c> on the produce side.
    /// </remarks>
    internal const string ContentBasedDeduplicationKey = "bw.sqs.content-based-deduplication";

    /// <summary>
    /// Parses <see cref="QueueDeclaration.Arguments"/> into a <see cref="SqsQueueSpec"/>.
    /// When <paramref name="queue"/> has no <c>Arguments</c>, returns defaults
    /// (30-second visibility, 20-second wait, no FIFO, maxReceiveCount=5).
    /// </summary>
    /// <param name="queue">The queue declaration to parse.</param>
    /// <returns>A <see cref="SqsQueueSpec"/> with the extracted or default values.</returns>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when <c>bw.sqs.visibility-timeout</c> is not a parseable <see cref="TimeSpan"/>,
    /// <c>bw.sqs.wait-time-seconds</c> is not an integer in range 0–20,
    /// <c>bw.sqs.fifo</c> is not a parseable boolean, or
    /// <c>bw.sqs.max-receive-count</c> is not a positive integer.
    /// </exception>
    internal static SqsQueueSpec Parse(QueueDeclaration queue)
    {
        IReadOnlyDictionary<string, object>? args = queue.Arguments;

        if (args is null || args.Count == 0)
        {
            return DefaultSpec();
        }

        TimeSpan visibilityTimeout = TimeSpan.FromSeconds(30);
        int waitTimeSeconds = 20;
        bool isFifo = false;
        int maxReceiveCount = 5;
        bool contentBasedDeduplication = false;

        foreach ((string key, object value) in args)
        {
            if (key == VisibilityTimeout)
            {
                visibilityTimeout = ParseTimeSpan(key, value);
            }
            else if (key == WaitTimeSecondsKey)
            {
                waitTimeSeconds = ParseInt32InRange(key, value, min: 0, max: 20,
                    expectedDescription: "An integer in the range 0–20");
            }
            else if (key == FifoKey)
            {
                isFifo = ParseBool(key, value);
            }
            else if (key == MaxReceiveCountKey)
            {
                maxReceiveCount = ParseInt32InRange(key, value, min: 1, max: int.MaxValue,
                    expectedDescription: "An integer >= 1");
            }
            else if (key == ContentBasedDeduplicationKey)
            {
                contentBasedDeduplication = ParseBool(key, value);
            }
            // Unknown bw.sqs.* keys are silently ignored (forward-compatible).
        }

        return new SqsQueueSpec(
            visibilityTimeout, waitTimeSeconds, isFifo, maxReceiveCount, contentBasedDeduplication);
    }

    private static SqsQueueSpec DefaultSpec() =>
        new(
            VisibilityTimeout: TimeSpan.FromSeconds(30),
            WaitTimeSeconds: 20,
            IsFifo: false,
            MaxReceiveCount: 5,
            ContentBasedDeduplication: false);

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

    private static TimeSpan ParseTimeSpan(string key, object value)
    {
        if (value is TimeSpan ts)
        {
            return ts;
        }

        string? str = value.ToString();

        if (str is not null && TimeSpan.TryParse(str, CultureInfo.InvariantCulture, out TimeSpan parsed))
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
