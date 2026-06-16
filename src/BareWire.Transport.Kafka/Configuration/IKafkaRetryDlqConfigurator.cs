namespace BareWire.Transport.Kafka.Configuration;

/// <summary>
/// Fluent API for configuring the Kafka retry-topic + DLQ-topic pattern (R1.3).
/// Obtained via <see cref="IKafkaConfigurator.ConfigureRetryDlq"/>.
/// </summary>
/// <remarks>
/// The pattern is <b>opt-in</b>: it remains disabled until <see cref="Enable"/> is called. While
/// disabled, <c>SettleAsync(Defer)</c> throws <see cref="NotSupportedException"/> and
/// <c>SettleAsync(Reject)</c> logs and does not store the offset (R1.2 behaviour). See ADR-010.
/// </remarks>
public interface IKafkaRetryDlqConfigurator
{
    /// <summary>
    /// Enables the retry/DLQ pattern. Without this call the pattern stays disabled (opt-in).
    /// </summary>
    void Enable();

    /// <summary>
    /// Sets the maximum number of retry attempts before a message is dead-lettered.
    /// Defaults to <c>3</c> when not called.
    /// </summary>
    /// <param name="maxRetries">The retry cap. Must be &gt;= 0.</param>
    void MaxRetries(int maxRetries);

    /// <summary>
    /// Sets the suffix appended to the source topic to form the retry-topic name.
    /// Defaults to <c>.retry</c>. Only characters in <c>[a-zA-Z0-9._-]</c> are permitted.
    /// </summary>
    /// <param name="suffix">The retry-topic suffix.</param>
    void RetryTopicSuffix(string suffix);

    /// <summary>
    /// Sets the suffix appended to the source topic to form the DLQ-topic name.
    /// Defaults to <c>.DLQ</c>. Only characters in <c>[a-zA-Z0-9._-]</c> are permitted.
    /// </summary>
    /// <param name="suffix">The DLQ-topic suffix.</param>
    void DlqTopicSuffix(string suffix);

    /// <summary>
    /// Configures the exponential backoff applied to retry republication
    /// (<c>delay = baseDelay * multiplier^(attempt-1)</c>, capped at <paramref name="maxDelay"/>).
    /// Defaults to base 1s, multiplier 2.0, max 5m.
    /// </summary>
    /// <param name="baseDelay">The base delay for the first retry. Must be positive.</param>
    /// <param name="multiplier">The exponential multiplier. Must be &gt;= 1.</param>
    /// <param name="maxDelay">The maximum delay (cap). Must be &gt;= <paramref name="baseDelay"/>.</param>
    void Backoff(TimeSpan baseDelay, double multiplier, TimeSpan maxDelay);
}
