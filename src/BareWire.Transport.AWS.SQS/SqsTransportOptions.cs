using System.Globalization;
using BareWire.Abstractions.Exceptions;

namespace BareWire.Transport.AWS.SQS;

/// <summary>
/// Configuration options for the Amazon SQS transport adapter.
/// Apply via <see cref="ServiceCollectionExtensions.AddBareWireSqs"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three authentication modes are supported — selected by <see cref="AuthMode"/>:
/// <list type="bullet">
/// <item>
/// <term><see cref="SqsAuthMode.DefaultChain"/> (default)</term>
/// <description>
/// The AWS SDK default credential chain: environment variables, shared credentials file,
/// EC2/ECS/Lambda IAM role, etc. Preferred for production — no secrets stored in options.
/// </description>
/// </item>
/// <item>
/// <term><see cref="SqsAuthMode.Explicit"/></term>
/// <description>
/// Explicit <see cref="AccessKeyId"/> / <see cref="SecretAccessKey"/> pair.
/// <b>Security (SEC-02):</b> <see cref="SecretAccessKey"/> is never logged, never included in
/// <see cref="ToString"/>, and never echoed in exception messages.
/// </description>
/// </item>
/// <item>
/// <term><see cref="SqsAuthMode.InstanceProfile"/> (R4.3)</term>
/// <description>
/// EC2 Instance Metadata Service (IMDS) / ECS task-role credentials. No static secrets
/// required. Optionally bind to a specific IAM role via <see cref="InstanceProfileRoleName"/>.
/// </description>
/// </item>
/// </list>
/// </para>
/// <para>
/// <b>TLS (SEC-01):</b> When <see cref="ServiceUrl"/> is set and its scheme is not <c>https</c>,
/// <see cref="Validate"/> will throw unless <see cref="AllowInsecureEndpoint"/> is explicitly
/// set to <see langword="true"/> (intended for LocalStack / integration-test environments only).
/// </para>
/// <para>
/// <b>Encryption at rest (R4.3):</b> SSE-SQS and SSE-KMS per-queue encryption is supported
/// via topology arguments (<c>bw.sqs.sse-managed</c>, <c>bw.sqs.kms-master-key-id</c>,
/// <c>bw.sqs.kms-data-key-reuse-period</c>). Disabled by default (opt-in).
/// </para>
/// </remarks>
internal sealed class SqsTransportOptions
{
    /// <summary>
    /// Gets or sets the authentication mode. Defaults to <see cref="SqsAuthMode.DefaultChain"/>.
    /// </summary>
    public SqsAuthMode AuthMode { get; set; } = SqsAuthMode.DefaultChain;

    /// <summary>
    /// Gets or sets the optional IAM role name used when <see cref="AuthMode"/> is
    /// <see cref="SqsAuthMode.InstanceProfile"/>. When non-empty, the SDK binds to this specific
    /// IAM role via IMDS; when empty, the role attached to the EC2 instance or ECS task is used.
    /// This is an identifier (not a secret) and may appear in diagnostic output (R4.3).
    /// </summary>
    public string InstanceProfileRoleName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the AWS Access Key ID used when <see cref="AuthMode"/> is
    /// <see cref="SqsAuthMode.Explicit"/>. This is an identifier (not a secret) and may appear
    /// in diagnostic output. Must not be <see langword="null"/> or empty in Explicit mode.
    /// </summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the AWS Secret Access Key used when <see cref="AuthMode"/> is
    /// <see cref="SqsAuthMode.Explicit"/>.
    /// </summary>
    /// <remarks>
    /// <b>Security (SEC-02):</b> This value is never logged, never included in
    /// <see cref="ToString"/>, and never echoed in exception messages.
    /// </remarks>
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the AWS region name (e.g. <c>eu-central-1</c>).
    /// Used with both <see cref="SqsAuthMode.DefaultChain"/> and <see cref="SqsAuthMode.Explicit"/>.
    /// When <see langword="null"/> or empty, the SDK resolves the region from environment
    /// (<c>AWS_DEFAULT_REGION</c>, instance metadata, etc.).
    /// </summary>
    public string RegionEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional custom SQS service endpoint URL (e.g. <c>https://localhost:4566</c>
    /// for LocalStack). When <see langword="null"/> or empty, the official AWS endpoint for
    /// <see cref="RegionEndpoint"/> is used.
    /// </summary>
    /// <remarks>
    /// <b>TLS enforcement (SEC-01):</b> If this URL uses the <c>http</c> scheme and
    /// <see cref="AllowInsecureEndpoint"/> is <see langword="false"/>, <see cref="Validate"/>
    /// will throw <see cref="BareWireConfigurationException"/>. Set
    /// <see cref="AllowInsecureEndpoint"/> to <see langword="true"/> only in test environments.
    /// </remarks>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a non-HTTPS <see cref="ServiceUrl"/> is permitted.
    /// Defaults to <see langword="false"/>. Set to <see langword="true"/> only for
    /// LocalStack / local integration-test environments (SEC-01 opt-out).
    /// </summary>
    public bool AllowInsecureEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the default SQS message visibility timeout applied to newly consumed messages
    /// when no queue-specific argument overrides it. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan DefaultVisibilityTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the SQS long-poll wait time in seconds. Valid range: 0–20.
    /// Defaults to <c>20</c> (maximum long-polling duration, minimises empty-receive calls).
    /// </summary>
    public int WaitTimeSeconds { get; set; } = 20;

    /// <summary>
    /// Gets or sets the maximum number of messages to retrieve per <c>ReceiveMessage</c> call.
    /// Valid range: 1–10 (SQS hard limit). Defaults to <c>10</c>.
    /// </summary>
    public int MaxNumberOfMessages { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum number of in-flight (consumed but not yet settled) messages
    /// tracked in <c>SqsInFlightRegistry</c>. Defaults to <c>100</c>.
    /// </summary>
    /// <remarks>
    /// When the registry is full, further messages are dropped (subject to the channel
    /// <c>FullMode</c>) to prevent unbounded memory growth (ADR-004 / PERF-3).
    /// </remarks>
    public int MaxInFlightMessages { get; set; } = 100;

    /// <summary>
    /// Gets or sets a value indicating whether FIFO queues use content-based deduplication.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see langword="true"/>, the broker computes <c>MessageDeduplicationId</c> from the SHA-256
    /// hash of the message body — no explicit <c>MessageDeduplicationId</c> is sent on the wire.
    /// This requires the SQS queue to have <c>ContentBasedDeduplication=true</c> (set at queue
    /// creation via <c>bw.sqs.content-based-deduplication</c> topology argument).
    /// </para>
    /// <para>
    /// When <see langword="false"/> (default), BareWire generates a deterministic
    /// <c>MessageDeduplicationId</c> from a SHA-256 hash of (<c>MessageGroupId</c> + body)
    /// unless an explicit <c>BW-MessageDeduplicationId</c> header is present.
    /// </para>
    /// </remarks>
    public bool EnableContentBasedDeduplication { get; set; }

    /// <summary>
    /// Returns a diagnostic representation of these options with secrets redacted to prevent
    /// accidental secret exposure in logs, exception messages, and diagnostic output (SEC-02).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="SecretAccessKey"/> is always shown as <c>[Redacted]</c>.</item>
    /// <item>
    /// <see cref="AccessKeyId"/> is a non-secret AWS identifier (analogous to
    /// <c>FullyQualifiedNamespace</c> in the ASB adapter) and is shown as-is.
    /// </item>
    /// </list>
    /// </remarks>
    public override string ToString() =>
        $"SqsTransportOptions {{ AuthMode = {AuthMode}, InstanceProfileRoleName = {InstanceProfileRoleName}, " +
        $"AccessKeyId = {AccessKeyId}, SecretAccessKey = [Redacted], RegionEndpoint = {RegionEndpoint}, " +
        $"ServiceUrl = {ServiceUrl ?? "null"}, AllowInsecureEndpoint = {AllowInsecureEndpoint}, " +
        $"DefaultVisibilityTimeout = {DefaultVisibilityTimeout}, WaitTimeSeconds = {WaitTimeSeconds}, " +
        $"MaxNumberOfMessages = {MaxNumberOfMessages}, MaxInFlightMessages = {MaxInFlightMessages}, " +
        $"EnableContentBasedDeduplication = {EnableContentBasedDeduplication} }}";

    /// <summary>
    /// Validates this options instance, throwing <see cref="BareWireConfigurationException"/>
    /// when required values are missing or out of range.
    /// </summary>
    /// <remarks>
    /// <para>Validation is mode-aware:</para>
    /// <list type="bullet">
    /// <item>
    /// <see cref="SqsAuthMode.Explicit"/> — <see cref="AccessKeyId"/> and
    /// <see cref="SecretAccessKey"/> must be non-empty.
    /// </item>
    /// <item>
    /// <see cref="SqsAuthMode.DefaultChain"/> — no credential fields required.
    /// </item>
    /// </list>
    /// <para>
    /// Range checks: <see cref="WaitTimeSeconds"/> 0–20; <see cref="MaxNumberOfMessages"/> 1–10.
    /// </para>
    /// <para>
    /// TLS check (SEC-01): if <see cref="ServiceUrl"/> is set and uses <c>http</c> scheme,
    /// <see cref="AllowInsecureEndpoint"/> must be <see langword="true"/> or this method throws.
    /// </para>
    /// </remarks>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when required values are missing or out of range. Exception messages never echo
    /// secrets (SEC-02): <see cref="SecretAccessKey"/> is never included in exception detail.
    /// </exception>
    public void Validate()
    {
        if (AuthMode == SqsAuthMode.Explicit)
        {
            if (string.IsNullOrEmpty(AccessKeyId))
            {
                throw new BareWireConfigurationException(
                    optionName: nameof(AccessKeyId),
                    optionValue: string.Empty,
                    expectedValue: "A non-empty AWS Access Key ID when AuthMode is Explicit");
            }

            if (string.IsNullOrEmpty(SecretAccessKey))
            {
                throw new BareWireConfigurationException(
                    optionName: nameof(SecretAccessKey),
                    optionValue: string.Empty,
                    expectedValue: "A non-empty AWS Secret Access Key when AuthMode is Explicit");
            }
        }

        if (WaitTimeSeconds < 0 || WaitTimeSeconds > 20)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(WaitTimeSeconds),
                optionValue: WaitTimeSeconds.ToString(CultureInfo.InvariantCulture),
                expectedValue: "An integer in the range 0–20 (SQS long-poll wait time)");
        }

        if (MaxNumberOfMessages < 1 || MaxNumberOfMessages > 10)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(MaxNumberOfMessages),
                optionValue: MaxNumberOfMessages.ToString(CultureInfo.InvariantCulture),
                expectedValue: "An integer in the range 1–10 (SQS ReceiveMessage hard limit)");
        }

        // SEC-01: enforce HTTPS for non-test endpoints.
        if (!string.IsNullOrEmpty(ServiceUrl) &&
            Uri.TryCreate(ServiceUrl, UriKind.Absolute, out Uri? parsedUrl) &&
            string.Equals(parsedUrl.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !AllowInsecureEndpoint)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(ServiceUrl),
                optionValue: ServiceUrl,
                expectedValue: "An HTTPS URL, or set AllowInsecureEndpoint = true to opt out " +
                               "(LocalStack / test environments only). " +
                               "Using http transmits credentials in plain text (SEC-01).");
        }
    }
}
