namespace BareWire.Transport.AzureServiceBus.Configuration;

/// <summary>
/// Provides a fluent API for configuring the Azure Service Bus transport adapter.
/// Obtained via <see cref="ServiceCollectionExtensions.AddBareWireAzureServiceBus"/>.
/// </summary>
/// <remarks>
/// R2.1: connection-string auth, prefetch count, and max concurrent calls.
/// R2.2: session support — <see cref="UseSessions"/>, <see cref="SessionIdleTimeout"/>,
/// <see cref="MaxAutoLockRenewDuration"/>.
/// Full SAS token refresh and Azure Entra ID (<c>DefaultAzureCredential</c>) are deferred to R2.4.
/// Native scheduled messages (R2.3) will extend this interface.
/// </remarks>
public interface IAzureServiceBusConfigurator
{
    /// <summary>
    /// Configures the Azure Service Bus connection string (SAS connection string).
    /// Must be called before the bus is started.
    /// </summary>
    /// <param name="connectionString">
    /// The Service Bus connection string containing the namespace endpoint and SAS credentials.
    /// Format: <c>Endpoint=sb://&lt;namespace&gt;.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...</c>
    /// Must not be <see langword="null"/> or empty. Never logged or stored in diagnostic output.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="connectionString"/> is <see langword="null"/> or empty.
    /// </exception>
    void ConnectionString(string connectionString);

    /// <summary>
    /// Configures the number of messages the receiver will pre-fetch from the broker.
    /// Defaults to <c>0</c> (no pre-fetch) when not called.
    /// </summary>
    /// <param name="prefetchCount">
    /// The pre-fetch count. A value of <c>0</c> disables pre-fetching (safest for PeekLock).
    /// </param>
    /// <remarks>
    /// See <c>AzureServiceBusTransportOptions.PrefetchCount</c> for lock-expiry risk guidance.
    /// </remarks>
    void PrefetchCount(int prefetchCount);

    /// <summary>
    /// Configures the maximum number of messages processed concurrently per consumer.
    /// Defaults to <c>1</c> when not called.
    /// </summary>
    /// <param name="maxConcurrentCalls">The maximum concurrent message count. Must be at least 1.</param>
    void MaxConcurrentCalls(int maxConcurrentCalls);

    // ── Session support (R2.2) ────────────────────────────────────────────────

    /// <summary>
    /// Enables session-based (FIFO per <c>SessionId</c>) message processing.
    /// </summary>
    /// <param name="maxConcurrentSessions">
    /// The maximum number of sessions to accept and process concurrently. Must be at least 1.
    /// Defaults to <c>1</c>. Each active session occupies one long-running task and one bounded
    /// channel; increase for workloads with many independent sessions.
    /// </param>
    /// <remarks>
    /// <para>
    /// The target queue <b>must</b> have been created with <c>RequiresSession = true</c>.
    /// Use the <c>bw.asb.requires-session</c> topology argument in <c>TopologyDeclaration</c>.
    /// </para>
    /// <para>
    /// Session messages on the produce path are routed by stamping
    /// <c>ServiceBusMessage.SessionId</c> from the <c>BW-SessionId</c> header (explicit) or the
    /// <c>correlation-id</c> header (fallback), enabling automatic per-saga-instance FIFO ordering.
    /// </para>
    /// </remarks>
    void UseSessions(int maxConcurrentSessions = 1);

    /// <summary>
    /// Configures the maximum time a session may be idle (no messages) before it is released.
    /// When not called, the SDK default is used (approximately 1 second).
    /// </summary>
    /// <param name="idleTimeout">
    /// The idle timeout. Must be positive (<c>&gt; TimeSpan.Zero</c>).
    /// </param>
    void SessionIdleTimeout(TimeSpan idleTimeout);

    /// <summary>
    /// Configures the maximum total duration for which BareWire proactively renews the session
    /// lock via a background task. Defaults to <c>5 minutes</c> when not called.
    /// </summary>
    /// <param name="duration">
    /// The maximum renew duration. Must be non-negative (<c>&gt;= TimeSpan.Zero</c>).
    /// Set to <c>TimeSpan.Zero</c> to disable background session-lock renewal entirely.
    /// </param>
    /// <remarks>
    /// This is a BareWire-level knob, not an SDK property — <c>ServiceBusSessionReceiverOptions</c>
    /// has no equivalent in SDK 7.20.1. BareWire's background renew loop calls
    /// <c>ServiceBusSessionReceiver.RenewSessionLockAsync()</c> at intervals derived from
    /// <c>SessionLockedUntil</c> (≈ half the remaining lock window, minus a safety margin).
    /// </remarks>
    void MaxAutoLockRenewDuration(TimeSpan duration);
}
