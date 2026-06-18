namespace BareWire.Transport.Google.PubSub.Configuration;

/// <summary>
/// Provides a fluent API for configuring the Google Cloud Pub/Sub transport adapter.
/// Obtained via <see cref="ServiceCollectionExtensions.AddBareWirePubSub"/>.
/// </summary>
/// <remarks>
/// <para>
/// Authentication modes:
/// <list type="bullet">
/// <item><see cref="UseApplicationDefaultCredentials"/> — Google ADC (preferred for production).</item>
/// <item><see cref="UseServiceAccountJson(string)"/> — explicit service account JSON key file or inline JSON.</item>
/// <item><see cref="UseEmulator"/> — plaintext gRPC to a local Pub/Sub emulator (test only).</item>
/// </list>
/// </para>
/// <para>
/// <b>Production guidance:</b> prefer <see cref="UseApplicationDefaultCredentials"/> with
/// Workload Identity (GKE), Compute Engine metadata server, or the <c>GOOGLE_APPLICATION_CREDENTIALS</c>
/// environment variable pointing to a service account key file.
/// </para>
/// </remarks>
public interface IPubSubConfigurator
{
    /// <summary>
    /// Configures the adapter to use Google Application Default Credentials (ADC).
    /// This is the default and the preferred mode for production deployments.
    /// </summary>
    void UseApplicationDefaultCredentials();

    /// <summary>
    /// Configures the adapter to use a service account JSON key from a file path.
    /// </summary>
    /// <param name="jsonFilePath">
    /// The path to the service account JSON key file. Must not be <see langword="null"/> or empty.
    /// <b>Security (SEC-02):</b> the file path is shown in diagnostic output; the file contents
    /// are never logged.
    /// </param>
    void UseServiceAccountJson(string jsonFilePath);

    /// <summary>
    /// Configures the adapter to use an inline service account JSON key string.
    /// </summary>
    /// <param name="jsonContent">
    /// The service account JSON key content. Must not be <see langword="null"/> or empty.
    /// <b>Security (SEC-02):</b> never logged, never included in <c>ToString()</c>.
    /// </param>
    void UseServiceAccountJsonContent(string jsonContent);

    /// <summary>
    /// Configures the adapter to connect to a local Pub/Sub emulator using insecure gRPC transport.
    /// Use only for local development and integration tests (SEC-01 opt-out).
    /// </summary>
    /// <param name="endpoint">
    /// The emulator endpoint, e.g. <c>localhost:8085</c>.
    /// Must not be <see langword="null"/> or empty.
    /// </param>
    void UseEmulator(string endpoint);

    /// <summary>
    /// Configures the Google Cloud project ID. Required in all authentication modes.
    /// </summary>
    /// <param name="projectId">
    /// The Google Cloud project ID. Must not be <see langword="null"/> or empty.
    /// This is a non-secret identifier and may appear in diagnostic output.
    /// </param>
    void ProjectId(string projectId);

    /// <summary>
    /// Configures the default acknowledgement deadline applied to subscriptions at topology deploy time.
    /// Valid range: 10 – 600 seconds. Defaults to 60 seconds when not called.
    /// </summary>
    /// <param name="deadline">Must be between 10 and 600 seconds.</param>
    void AckDeadline(TimeSpan deadline);

    /// <summary>
    /// Configures the maximum number of messages retrieved per <c>PullAsync</c> call.
    /// Acts as the cap on outstanding unacknowledged messages (flow control 1:1). Defaults to 1000.
    /// </summary>
    /// <param name="max">Must be at least 1.</param>
    void MaxOutstandingMessages(int max);

    /// <summary>
    /// Configures the maximum total byte size of in-flight message bodies.
    /// Defaults to 67,108,864 bytes (64 MiB).
    /// </summary>
    /// <param name="maxBytes">Must be at least 1.</param>
    void MaxOutstandingBytes(long maxBytes);

    /// <summary>
    /// Configures the maximum number of concurrent in-flight messages tracked by the registry.
    /// Defaults to 100. When the limit is reached, further messages are not consumed until
    /// capacity frees up (PERF-3 / ADR-004).
    /// </summary>
    /// <param name="max">Must be at least 1.</param>
    void MaxInFlightMessages(int max);

    /// <summary>
    /// Enables message ordering for subscriptions created during topology deployment.
    /// When called, subscriptions are created with <c>enable_message_ordering = true</c>.
    /// </summary>
    void EnableMessageOrdering();
}
