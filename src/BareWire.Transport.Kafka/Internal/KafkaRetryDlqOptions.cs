using BareWire.Abstractions.Exceptions;

namespace BareWire.Transport.Kafka.Internal;

/// <summary>
/// Configuration for the emulated retry-topic + DLQ-topic pattern (R1.3).
/// Kafka has no native DLQ or delayed redelivery — failed messages are republished to a
/// dedicated retry-topic (with exponential backoff) or, on rejection / retry exhaustion,
/// to a DLQ-topic. See ADR-010.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in (ADR-002 spirit):</b> the pattern is disabled by default (<see cref="Enabled"/> =
/// <see langword="false"/>). When disabled, <c>SettleAsync(Defer)</c> still throws
/// <see cref="NotSupportedException"/> and <c>SettleAsync(Reject)</c> logs + no-store, exactly as
/// in R1.2. Enabling it (via <c>IKafkaRetryDlqConfigurator.Enable()</c>) wires republication.
/// </para>
/// <para>
/// <b>Trust boundary (SEC-1, ADR-010):</b> the wire-supplied <c>BW-RetryCount</c> header is
/// untrusted on the source topic; the settlement router clamps it to
/// <c>[0, <see cref="MaxRetryCount"/>]</c> before routing so a spoofed value cannot force a
/// premature DLQ or an unbounded retry loop (poison amplification).
/// </para>
/// </remarks>
internal sealed class KafkaRetryDlqOptions
{
    /// <summary>
    /// Set of characters permitted in topic-name suffixes. Kafka topic names are restricted to
    /// <c>[a-zA-Z0-9._-]</c>; validating the suffix prevents constructing invalid topic names
    /// (low-severity topic-name-injection hardening, ADR-010 §security).
    /// </summary>
    private static readonly System.Buffers.SearchValues<char> AllowedSuffixChars =
        System.Buffers.SearchValues.Create(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-");

    /// <summary>
    /// Gets or sets a value indicating whether the retry/DLQ pattern is enabled.
    /// Defaults to <see langword="false"/> (opt-in).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of retry attempts before a message is dead-lettered.
    /// A message whose clamped <c>BW-RetryCount</c> has reached this value is routed to the
    /// DLQ-topic on the next failure. Defaults to <c>3</c>.
    /// </summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets the suffix appended to the source topic name to form the retry-topic name
    /// (e.g. <c>orders</c> → <c>orders.retry</c>). Defaults to <c>.retry</c>.
    /// </summary>
    public string RetryTopicSuffix { get; set; } = ".retry";

    /// <summary>
    /// Gets or sets the suffix appended to the source topic name to form the DLQ-topic name
    /// (e.g. <c>orders</c> → <c>orders.DLQ</c>). Defaults to <c>.DLQ</c>.
    /// </summary>
    public string DlqTopicSuffix { get; set; } = ".DLQ";

    /// <summary>
    /// Gets or sets the base delay for the first retry attempt. Defaults to 1 second.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the exponential backoff multiplier applied per attempt
    /// (<c>delay = BaseDelay * Multiplier^(attempt-1)</c>, capped at <see cref="MaxDelay"/>).
    /// Must be &gt;= 1. Defaults to <c>2.0</c>.
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets the maximum backoff delay (cap). Defaults to 5 minutes.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Validates this options instance, throwing <see cref="BareWireConfigurationException"/> when
    /// any value is out of range. Only invoked when <see cref="Enabled"/> is <see langword="true"/>
    /// (a disabled instance carries defaults and is never used for routing).
    /// </summary>
    /// <exception cref="BareWireConfigurationException">Thrown when a value is invalid.</exception>
    public void Validate()
    {
        if (MaxRetryCount < 0)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(MaxRetryCount),
                optionValue: MaxRetryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                expectedValue: "A non-negative retry count (e.g. 3)");
        }

        ValidateSuffix(RetryTopicSuffix, nameof(RetryTopicSuffix));
        ValidateSuffix(DlqTopicSuffix, nameof(DlqTopicSuffix));

        if (BackoffMultiplier < 1.0)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(BackoffMultiplier),
                optionValue: BackoffMultiplier.ToString(System.Globalization.CultureInfo.InvariantCulture),
                expectedValue: "A multiplier >= 1.0 (e.g. 2.0)");
        }

        if (BaseDelay <= TimeSpan.Zero)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(BaseDelay),
                optionValue: BaseDelay.ToString(),
                expectedValue: "A positive base delay (e.g. 00:00:01)");
        }

        if (MaxDelay < BaseDelay)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(MaxDelay),
                optionValue: MaxDelay.ToString(),
                expectedValue: $"A delay >= BaseDelay ({BaseDelay})");
        }
    }

    private static void ValidateSuffix(string suffix, string optionName)
    {
        if (string.IsNullOrEmpty(suffix))
        {
            throw new BareWireConfigurationException(
                optionName: optionName,
                optionValue: suffix ?? "(null)",
                expectedValue: "A non-empty topic suffix (e.g. .retry)");
        }

        if (suffix.AsSpan().ContainsAnyExcept(AllowedSuffixChars))
        {
            throw new BareWireConfigurationException(
                optionName: optionName,
                optionValue: suffix,
                expectedValue: "A suffix containing only [a-zA-Z0-9._-] (valid Kafka topic characters)");
        }
    }
}
