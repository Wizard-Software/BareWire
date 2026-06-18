namespace BareWire.Transport.AWS.SQS.Configuration;

/// <summary>
/// Provides a fluent API for configuring the Amazon SQS transport adapter.
/// Obtained via <see cref="ServiceCollectionExtensions.AddBareWireSqs"/>.
/// </summary>
/// <remarks>
/// <para>
/// Authentication modes:
/// <list type="bullet">
/// <item><see cref="UseDefaultCredentials"/> — AWS SDK default credential chain (preferred for production).</item>
/// <item><see cref="UseExplicitCredentials"/> — static Access Key ID + Secret Access Key pair.</item>
/// </list>
/// </para>
/// <para>
/// <b>Production guidance:</b> prefer <see cref="UseDefaultCredentials"/> with IAM roles
/// (EC2 instance profile, ECS task role, Lambda execution role). For explicit IMDS binding,
/// use <see cref="UseInstanceProfileCredentials"/> (R4.3).
/// </para>
/// </remarks>
public interface ISqsConfigurator
{
    /// <summary>
    /// Configures the adapter to use the AWS SDK default credential chain
    /// (environment variables, shared credentials file, EC2/ECS/Lambda IAM role, etc.).
    /// This is the default and the preferred mode for production deployments.
    /// </summary>
    void UseDefaultCredentials();

    /// <summary>
    /// Configures the adapter to use an explicit Access Key ID and Secret Access Key.
    /// </summary>
    /// <param name="accessKeyId">
    /// The AWS Access Key ID. Must not be <see langword="null"/> or empty.
    /// This is an identifier (not a secret) and may appear in diagnostic output.
    /// </param>
    /// <param name="secretAccessKey">
    /// The AWS Secret Access Key. Must not be <see langword="null"/> or empty.
    /// <b>Security (SEC-02):</b> never logged, never included in <c>ToString()</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when either parameter is <see langword="null"/> or empty.
    /// </exception>
    void UseExplicitCredentials(string accessKeyId, string secretAccessKey);

    /// <summary>
    /// Configures the adapter to use EC2 Instance Metadata Service (IMDS) or ECS task-role
    /// credentials, binding to the IAM role assigned to the current instance or task.
    /// </summary>
    /// <param name="roleName">
    /// Optional IAM role name. When non-<see langword="null"/> and non-empty, the SDK binds to
    /// this specific role via IMDS. When <see langword="null"/> or empty, the default role
    /// assigned to the instance or ECS task is used (recommended for most deployments).
    /// This value is an identifier (not a secret) and may appear in diagnostic output (R4.3).
    /// </param>
    /// <remarks>
    /// No static secrets are required — credentials are fetched from IMDS and refreshed
    /// automatically by the SDK before expiry. Prefer this over <see cref="UseExplicitCredentials"/>
    /// for EC2 / ECS production deployments.
    /// </remarks>
    void UseInstanceProfileCredentials(string? roleName = null);

    /// <summary>
    /// Configures the AWS region (e.g. <c>eu-central-1</c>, <c>us-east-1</c>).
    /// When not called, the SDK resolves the region from the environment
    /// (<c>AWS_DEFAULT_REGION</c>, instance metadata, etc.).
    /// </summary>
    /// <param name="regionName">The AWS region name. Must not be <see langword="null"/> or empty.</param>
    void Region(string regionName);

    /// <summary>
    /// Configures a custom SQS service endpoint URL (e.g. <c>https://localhost:4566</c> for LocalStack).
    /// When not called, the official AWS endpoint for the configured region is used.
    /// </summary>
    /// <param name="url">
    /// The custom endpoint URL. <b>TLS enforcement (SEC-01):</b> if the URL uses the <c>http</c>
    /// scheme, <see cref="AllowInsecureEndpoint"/> must also be called or validation will throw.
    /// </param>
    void ServiceUrl(string url);

    /// <summary>
    /// Opts out of TLS enforcement for the configured <see cref="ServiceUrl"/>.
    /// Use only for LocalStack / local integration-test environments (SEC-01 opt-out).
    /// </summary>
    void AllowInsecureEndpoint();

    /// <summary>
    /// Configures the default message visibility timeout. Defaults to 30 seconds when not called.
    /// </summary>
    /// <param name="timeout">Must be positive.</param>
    void VisibilityTimeout(TimeSpan timeout);

    /// <summary>
    /// Configures the SQS long-poll wait time in seconds. Valid range: 0–20. Defaults to 20.
    /// </summary>
    /// <param name="seconds">The wait time in seconds.</param>
    void WaitTimeSeconds(int seconds);

    /// <summary>
    /// Configures the maximum number of messages retrieved per <c>ReceiveMessage</c> call.
    /// Valid range: 1–10 (SQS hard limit). Defaults to 10.
    /// </summary>
    /// <param name="count">The maximum number of messages to retrieve per call.</param>
    void MaxNumberOfMessages(int count);

    /// <summary>
    /// Configures the maximum number of concurrent in-flight messages tracked by the registry.
    /// Defaults to 100. When the limit is reached, further messages are not consumed until
    /// capacity frees up (PERF-3 / ADR-004).
    /// </summary>
    /// <param name="max">Must be at least 1.</param>
    void MaxInFlightMessages(int max);

    /// <summary>
    /// Enables content-based deduplication for FIFO queue produce.
    /// When called, BareWire does not generate an explicit <c>MessageDeduplicationId</c> —
    /// the broker computes the dedup id from a SHA-256 hash of the message body server-side.
    /// </summary>
    /// <remarks>
    /// Requires that the target SQS FIFO queue was created with <c>ContentBasedDeduplication=true</c>
    /// (set via the <c>bw.sqs.content-based-deduplication</c> topology argument).
    /// When not called, BareWire generates a deterministic dedup id from a SHA-256 hash of
    /// (<c>MessageGroupId</c> + body) unless an explicit <c>BW-MessageDeduplicationId</c> header
    /// is present.
    /// </remarks>
    void ContentBasedDeduplication();
}
