namespace BareWire.Transport.AzureServiceBus.Configuration;

/// <summary>
/// Provides a fluent API for configuring the Azure Service Bus transport adapter.
/// Obtained via <see cref="ServiceCollectionExtensions.AddBareWireAzureServiceBus"/>.
/// </summary>
/// <remarks>
/// <para>
/// Authentication modes (R2.4):
/// <list type="bullet">
/// <item><see cref="UseSasAuth"/> — Shared Access Signature via connection string.</item>
/// <item><see cref="UseEntraIdAuth"/> — Azure Entra ID via <see cref="Azure.Core.TokenCredential"/>.</item>
/// <item><see cref="ConnectionString"/> — legacy alias for <see cref="UseSasAuth"/>, preserved for backward compatibility.</item>
/// </list>
/// </para>
/// <para>
/// Session support (R2.2): <see cref="UseSessions"/>, <see cref="SessionIdleTimeout"/>,
/// <see cref="MaxAutoLockRenewDuration"/>.
/// </para>
/// <para>
/// Scheduling (R2.3): native scheduled messages via the Azure Service Bus broker.
/// </para>
/// </remarks>
public interface IAzureServiceBusConfigurator
{
    /// <summary>
    /// Configures SAS (Shared Access Signature) authentication via a connection string.
    /// Sets <c>AuthMode = Sas</c>. Semantically equivalent to calling <see cref="ConnectionString"/>
    /// and can be used interchangeably; <see cref="ConnectionString"/> is preserved for
    /// backward compatibility.
    /// </summary>
    /// <param name="connectionString">
    /// The Service Bus connection string containing the namespace endpoint and SAS credentials.
    /// Format: <c>Endpoint=sb://&lt;namespace&gt;.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...</c>
    /// Must not be <see langword="null"/> or empty. Never logged or stored in diagnostic output.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="connectionString"/> is <see langword="null"/> or empty.
    /// </exception>
    void UseSasAuth(string connectionString);

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
    /// <remarks>
    /// This method is preserved for backward compatibility with R2.1 code. New code should prefer
    /// <see cref="UseSasAuth"/> for clarity. Both methods set <c>AuthMode = Sas</c> and
    /// <c>ConnectionString</c> to the same value.
    /// </remarks>
    void ConnectionString(string connectionString);

    /// <summary>
    /// Configures Azure Entra ID authentication via a <see cref="Azure.Core.TokenCredential"/>
    /// against a fully-qualified namespace host. Sets <c>AuthMode = EntraId</c>.
    /// </summary>
    /// <param name="fullyQualifiedNamespace">
    /// The fully-qualified Azure Service Bus namespace host.
    /// Format: <c>&lt;namespace&gt;.servicebus.windows.net</c>.
    /// Must not be <see langword="null"/> or empty. The namespace host is a non-secret identifier
    /// and is safe to include in logs and diagnostic output.
    /// </param>
    /// <param name="credential">
    /// The <see cref="Azure.Core.TokenCredential"/> used to authenticate. Must not be
    /// <see langword="null"/>. Typically <c>new DefaultAzureCredential()</c> for Managed Identity
    /// or local developer credential chain.
    /// </param>
    /// <remarks>
    /// Token refresh is performed automatically by the Azure SDK
    /// (<c>Azure.Messaging.ServiceBus</c> 7.x); BareWire does not implement its own refresh loop.
    /// The credential object is never serialised, never logged, and never echoed in exception
    /// messages — it is represented by type name only in diagnostic output.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="fullyQualifiedNamespace"/> is <see langword="null"/> or empty.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="credential"/> is <see langword="null"/>.
    /// </exception>
    void UseEntraIdAuth(string fullyQualifiedNamespace, Azure.Core.TokenCredential credential);

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
