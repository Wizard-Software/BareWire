using System.Globalization;
using BareWire.Abstractions.Exceptions;

namespace BareWire.Transport.Google.PubSub;

/// <summary>
/// Configuration options for the Google Cloud Pub/Sub transport adapter.
/// Apply via <see cref="ServiceCollectionExtensions.AddBareWirePubSub"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three authentication modes are supported — selected by <see cref="AuthMode"/>:
/// <list type="bullet">
/// <item>
/// <term><see cref="PubSubAuthMode.ApplicationDefault"/> (default)</term>
/// <description>
/// Google Application Default Credentials (ADC): environment, gcloud CLI, Workload Identity,
/// Compute Engine metadata server. Preferred for production — no secrets stored in options.
/// </description>
/// </item>
/// <item>
/// <term><see cref="PubSubAuthMode.ServiceAccountJson"/></term>
/// <description>
/// Service account JSON key via file path (<see cref="ServiceAccountJsonPath"/>) or inline
/// JSON content (<see cref="ServiceAccountJson"/>).
/// <b>Security (SEC-02):</b> <see cref="ServiceAccountJson"/> is never logged, never included
/// in <see cref="ToString"/>, and never echoed in exception messages.
/// </description>
/// </item>
/// <item>
/// <term><see cref="PubSubAuthMode.EmulatorInsecure"/></term>
/// <description>
/// Plaintext gRPC to a local Pub/Sub emulator (<see cref="EmulatorEndpoint"/>). For local
/// development and integration tests only. <see cref="Validate"/> enforces that
/// <see cref="EmulatorEndpoint"/> is non-empty when this mode is chosen, and rejects a
/// non-empty <see cref="EmulatorEndpoint"/> under any other mode (SEC-3).
/// </description>
/// </item>
/// </list>
/// </para>
/// </remarks>
internal sealed class PubSubTransportOptions
{
    /// <summary>
    /// Gets or sets the authentication mode. Defaults to <see cref="PubSubAuthMode.ApplicationDefault"/>.
    /// </summary>
    public PubSubAuthMode AuthMode { get; set; } = PubSubAuthMode.ApplicationDefault;

    /// <summary>
    /// Gets or sets the Google Cloud project ID. Required in all modes.
    /// This is a non-secret identifier and may appear in diagnostic output.
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path to a service account JSON key file.
    /// Used when <see cref="AuthMode"/> is <see cref="PubSubAuthMode.ServiceAccountJson"/>.
    /// Must be non-empty when <see cref="ServiceAccountJson"/> is also empty in that mode.
    /// </summary>
    public string ServiceAccountJsonPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the inline service account JSON content.
    /// Used when <see cref="AuthMode"/> is <see cref="PubSubAuthMode.ServiceAccountJson"/>.
    /// Must be non-empty when <see cref="ServiceAccountJsonPath"/> is also empty in that mode.
    /// </summary>
    /// <remarks>
    /// <b>Security (SEC-02):</b> This value is never logged, never included in
    /// <see cref="ToString"/>, and never echoed in exception messages. The path
    /// <see cref="ServiceAccountJsonPath"/> is non-secret and shown as-is.
    /// </remarks>
    public string ServiceAccountJson { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Pub/Sub emulator endpoint (e.g. <c>localhost:8085</c>).
    /// Only valid when <see cref="AuthMode"/> is <see cref="PubSubAuthMode.EmulatorInsecure"/>.
    /// Setting this under any other auth mode causes <see cref="Validate"/> to throw (SEC-3).
    /// </summary>
    public string EmulatorEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default acknowledgement deadline applied to subscriptions at topology deploy time.
    /// Valid range: 10 – 600 seconds (Google Cloud Pub/Sub enforces this range).
    /// Defaults to 60 seconds.
    /// </summary>
    public TimeSpan DefaultAckDeadline { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the maximum number of messages retrieved per <c>PullAsync</c> call.
    /// Acts as the effective cap on outstanding unacknowledged messages (flow control 1:1).
    /// Defaults to 1000.
    /// </summary>
    public int MaxOutstandingMessages { get; set; } = 1_000;

    /// <summary>
    /// Gets or sets the maximum total byte size of in-flight message bodies.
    /// Used as an additional gate in the pull loop to bound memory usage.
    /// Defaults to 67,108,864 bytes (64 MiB), consistent with <c>FlowControlOptions.MaxInFlightBytes</c>.
    /// </summary>
    public long MaxOutstandingBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum number of in-flight (consumed but not yet settled) messages
    /// tracked in <c>PubSubInFlightRegistry</c>. Defaults to <c>100</c>.
    /// </summary>
    /// <remarks>
    /// When the registry is full, further messages are dropped (subject to the channel
    /// <c>FullMode</c>) to prevent unbounded memory growth (ADR-004 / PERF-3).
    /// </remarks>
    public int MaxInFlightMessages { get; set; } = 100;

    /// <summary>
    /// Gets or sets a value indicating whether message ordering is enabled for subscriptions.
    /// When <see langword="true"/>, subscriptions are created with <c>enable_message_ordering</c>.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool EnableMessageOrdering { get; set; }

    /// <summary>
    /// Returns a diagnostic representation of these options with secrets redacted to prevent
    /// accidental secret exposure in logs, exception messages, and diagnostic output (SEC-02).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="ServiceAccountJson"/> is always shown as <c>[Redacted]</c>.</item>
    /// <item>
    /// <see cref="ServiceAccountJsonPath"/> is a filesystem path (operator-supplied identifier,
    /// not the secret itself) and is shown as-is.
    /// </item>
    /// <item><see cref="ProjectId"/> is a non-secret GCP identifier and is shown as-is.</item>
    /// </list>
    /// </remarks>
    public override string ToString() =>
        $"PubSubTransportOptions {{ AuthMode = {AuthMode}, ProjectId = {ProjectId}, " +
        $"ServiceAccountJsonPath = {ServiceAccountJsonPath}, ServiceAccountJson = [Redacted], " +
        $"EmulatorEndpoint = {EmulatorEndpoint}, DefaultAckDeadline = {DefaultAckDeadline}, " +
        $"MaxOutstandingMessages = {MaxOutstandingMessages}, MaxOutstandingBytes = {MaxOutstandingBytes}, " +
        $"MaxInFlightMessages = {MaxInFlightMessages}, EnableMessageOrdering = {EnableMessageOrdering} }}";

    /// <summary>
    /// Validates this options instance, throwing <see cref="BareWireConfigurationException"/>
    /// when required values are missing or out of range.
    /// </summary>
    /// <remarks>
    /// <para>Validation is mode-aware:</para>
    /// <list type="bullet">
    /// <item>
    /// All modes: <see cref="ProjectId"/> must be non-empty.
    /// </item>
    /// <item>
    /// <see cref="PubSubAuthMode.ServiceAccountJson"/>: either <see cref="ServiceAccountJsonPath"/>
    /// or <see cref="ServiceAccountJson"/> must be non-empty.
    /// </item>
    /// <item>
    /// <see cref="PubSubAuthMode.EmulatorInsecure"/>: <see cref="EmulatorEndpoint"/> must be non-empty.
    /// </item>
    /// </list>
    /// <para>
    /// SEC-3 guard: a non-empty <see cref="EmulatorEndpoint"/> under any mode other than
    /// <see cref="PubSubAuthMode.EmulatorInsecure"/> is rejected (prevents silently downgrading
    /// production credentials to plaintext gRPC).
    /// </para>
    /// <para>
    /// Range checks: <see cref="DefaultAckDeadline"/> must be between 10 and 600 seconds.
    /// </para>
    /// </remarks>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when required values are missing or out of range. Exception messages never echo
    /// secrets (SEC-02): <see cref="ServiceAccountJson"/> content is never included in detail.
    /// </exception>
    public void Validate()
    {
        if (string.IsNullOrEmpty(ProjectId))
        {
            throw new BareWireConfigurationException(
                optionName: nameof(ProjectId),
                optionValue: string.Empty,
                expectedValue: "A non-empty Google Cloud project ID");
        }

        if (AuthMode == PubSubAuthMode.ServiceAccountJson)
        {
            if (string.IsNullOrEmpty(ServiceAccountJsonPath) && string.IsNullOrEmpty(ServiceAccountJson))
            {
                throw new BareWireConfigurationException(
                    optionName: nameof(ServiceAccountJson),
                    optionValue: string.Empty,
                    expectedValue: "A non-empty ServiceAccountJsonPath or ServiceAccountJson when AuthMode is ServiceAccountJson");
            }
        }

        if (AuthMode == PubSubAuthMode.EmulatorInsecure)
        {
            if (string.IsNullOrEmpty(EmulatorEndpoint))
            {
                throw new BareWireConfigurationException(
                    optionName: nameof(EmulatorEndpoint),
                    optionValue: string.Empty,
                    expectedValue: "A non-empty emulator endpoint (e.g. 'localhost:8085') when AuthMode is EmulatorInsecure");
            }
        }
        else if (!string.IsNullOrEmpty(EmulatorEndpoint))
        {
            // SEC-3: reject a stale emulator endpoint under non-emulator auth mode.
            // An EmulatorEndpoint set alongside ApplicationDefault or ServiceAccountJson would
            // silently use insecure credentials against the emulator, bypassing production TLS.
            throw new BareWireConfigurationException(
                optionName: nameof(EmulatorEndpoint),
                optionValue: EmulatorEndpoint,
                expectedValue: $"EmulatorEndpoint must be empty when AuthMode is {AuthMode}. " +
                               "Set AuthMode = EmulatorInsecure to connect to the emulator (SEC-3).");
        }

        double ackDeadlineSeconds = DefaultAckDeadline.TotalSeconds;
        if (ackDeadlineSeconds < 10 || ackDeadlineSeconds > 600)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(DefaultAckDeadline),
                optionValue: ackDeadlineSeconds.ToString(CultureInfo.InvariantCulture),
                expectedValue: "A TimeSpan between 10 and 600 seconds (Google Cloud Pub/Sub ack deadline range)");
        }
    }
}
