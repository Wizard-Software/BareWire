namespace BareWire.Transport.Google.PubSub;

/// <summary>
/// Selects the authentication strategy for the Google Cloud Pub/Sub transport adapter.
/// </summary>
public enum PubSubAuthMode
{
    /// <summary>
    /// Uses the Google Application Default Credentials (ADC) chain: environment variables
    /// (<c>GOOGLE_APPLICATION_CREDENTIALS</c>), gcloud CLI, Workload Identity, Compute Engine
    /// metadata server. Preferred for production deployments — no secrets stored in options.
    /// </summary>
    ApplicationDefault = 0,

    /// <summary>
    /// Uses a service account JSON key supplied via
    /// <see cref="PubSubTransportOptions.ServiceAccountJsonPath"/> (file path) or
    /// <see cref="PubSubTransportOptions.ServiceAccountJson"/> (inline JSON content).
    /// <b>Security (SEC-02):</b> the JSON content is never logged, never included in
    /// <see cref="PubSubTransportOptions.ToString"/>, and never echoed in exception messages.
    /// </summary>
    ServiceAccountJson = 1,

    /// <summary>
    /// Connects to a local Pub/Sub emulator endpoint (e.g. <c>localhost:8085</c>) using
    /// insecure (plaintext) gRPC transport. Intended for local development and integration
    /// tests only — never use in production (SEC-01 opt-out, SEC-3 guard enforced in Validate).
    /// </summary>
    EmulatorInsecure = 2,
}
