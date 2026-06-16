namespace BareWire.Transport.Kafka.Internal;

/// <summary>
/// Pure topic-naming policy for the retry-topic + DLQ-topic pattern (R1.3).
/// Resolves the retry/DLQ topic name from a source topic name using the configured suffixes.
/// </summary>
/// <remarks>
/// Resolution is <b>idempotent</b>: a source name that already ends with the target suffix is
/// returned unchanged (so re-routing a message already on the retry-topic does not produce
/// <c>orders.retry.retry</c>). The retry/DLQ origin is tracked via the <c>BW-OriginalTopic</c>
/// header, not by mangling the name further.
/// </remarks>
internal static class RetryDlqTopicNamePolicy
{
    /// <summary>
    /// Resolves the retry-topic name for the given source topic
    /// (e.g. <c>orders</c> + suffix <c>.retry</c> → <c>orders.retry</c>).
    /// </summary>
    /// <param name="sourceTopic">The source topic name. Must not be null or empty.</param>
    /// <param name="options">The retry/DLQ options carrying the suffix.</param>
    /// <returns>The retry-topic name.</returns>
    internal static string ResolveRetryTopic(string sourceTopic, KafkaRetryDlqOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceTopic);
        ArgumentNullException.ThrowIfNull(options);

        return AppendSuffixIdempotent(sourceTopic, options.RetryTopicSuffix);
    }

    /// <summary>
    /// Resolves the DLQ-topic name for the given source topic
    /// (e.g. <c>orders</c> + suffix <c>.DLQ</c> → <c>orders.DLQ</c>).
    /// </summary>
    /// <param name="sourceTopic">The source topic name. Must not be null or empty.</param>
    /// <param name="options">The retry/DLQ options carrying the suffix.</param>
    /// <returns>The DLQ-topic name.</returns>
    internal static string ResolveDlqTopic(string sourceTopic, KafkaRetryDlqOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceTopic);
        ArgumentNullException.ThrowIfNull(options);

        return AppendSuffixIdempotent(sourceTopic, options.DlqTopicSuffix);
    }

    private static string AppendSuffixIdempotent(string topic, string suffix) =>
        topic.EndsWith(suffix, StringComparison.Ordinal)
            ? topic
            : topic + suffix;
}
