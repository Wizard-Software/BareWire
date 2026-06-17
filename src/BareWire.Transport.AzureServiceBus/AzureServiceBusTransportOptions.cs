using BareWire.Abstractions.Exceptions;

namespace BareWire.Transport.AzureServiceBus;

/// <summary>
/// Configuration options for the Azure Service Bus transport adapter.
/// Apply via <see cref="ServiceCollectionExtensions.AddBareWireAzureServiceBus"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope note (R2.1):</b> R2.1 supports only connection-string-based authentication.
/// Full SAS token refresh and Azure Entra ID (<c>DefaultAzureCredential</c>) are deferred to R2.4.
/// Azure Service Bus uses AMQP-over-TLS (port 5671) by default — no credential is transmitted
/// in plaintext. Do not use this adapter against a production namespace until R2.4 is complete.
/// </para>
/// </remarks>
internal sealed class AzureServiceBusTransportOptions
{
    /// <summary>
    /// Gets or sets the Service Bus connection string, including the SAS key.
    /// Format: <c>Endpoint=sb://&lt;namespace&gt;.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...</c>
    /// </summary>
    /// <remarks>
    /// <b>Security (SEC-02/SEC-06):</b> This property contains a secret (<c>SharedAccessKey</c>).
    /// It is never logged, never included in <see cref="ToString"/>, and never echoed in exception
    /// messages. See <see cref="ToString"/> for the redacted representation.
    /// </remarks>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of messages the Service Bus receiver will pre-fetch from the broker
    /// into a local buffer before they are requested by the polling loop.
    /// Defaults to <c>0</c> (no pre-fetch — safest setting for PeekLock).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>R-3 (prefetch vs lock-duration):</b> With PeekLock, pre-fetched messages have their
    /// lock timer started immediately upon pre-fetch. When back-pressure pauses the polling loop
    /// the locks on buffered messages may expire before they are settled, causing silent redelivery
    /// and eventually DLQ after <c>max-delivery-count</c> is exhausted.
    /// </para>
    /// <para>
    /// Recommendation: keep <c>PrefetchCount &lt;= InternalQueueCapacity</c>. For session-enabled
    /// endpoints, BareWire automatically runs a background renew task controlled by
    /// <see cref="MaxAutoLockRenewDuration"/> — see that property for details.
    /// </para>
    /// </remarks>
    public int PrefetchCount { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of messages processed concurrently per consumer instance.
    /// Defaults to <c>1</c> (sequential, consistent with bounded back-pressure under ADR-004).
    /// </summary>
    /// <remarks>
    /// Increase only when the message handler is I/O-bound and you have validated that concurrent
    /// processing is safe for your workload. Higher values increase memory pressure and lock
    /// expiry risk in PeekLock mode.
    /// </remarks>
    public int MaxConcurrentCalls { get; set; } = 1;

    // ── Session options (R2.2) ────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets a value indicating whether session-based (FIFO per <c>SessionId</c>) processing
    /// is enabled. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, <c>ConsumeAsync</c> uses <c>ServiceBusSessionReceiver</c> via
    /// <c>AcceptNextSessionAsync</c> instead of a plain <c>ServiceBusReceiver</c>.
    /// The target queue <b>must</b> have been created with <c>RequiresSession = true</c>
    /// (use the <c>bw.asb.requires-session</c> topology argument).
    /// Configure via <c>IAzureServiceBusConfigurator.UseSessions()</c>.
    /// </remarks>
    public bool EnableSessions { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of sessions accepted and processed concurrently.
    /// Defaults to <c>1</c>.
    /// </summary>
    /// <remarks>
    /// Each active session occupies one long-running task and one bounded channel.
    /// Increase to improve throughput when the workload has many independent sessions.
    /// Must be <c>&gt;= 1</c>; validated by <see cref="Validate"/>.
    /// </remarks>
    public int MaxConcurrentSessions { get; set; } = 1;

    /// <summary>
    /// Gets or sets the maximum time a session may be idle (no messages to receive) before it
    /// is released and a new <c>AcceptNextSessionAsync</c> is attempted.
    /// Defaults to <see langword="null"/> (use the SDK default, approximately 1 second idle before release).
    /// </summary>
    /// <remarks>
    /// When set, must be <c>&gt; <see cref="TimeSpan.Zero"/></c>; validated by <see cref="Validate"/>.
    /// </remarks>
    public TimeSpan? SessionIdleTimeout { get; set; }

    /// <summary>
    /// Gets or sets the maximum total duration for which BareWire will proactively renew the
    /// session lock via a background task. Defaults to <c>5 minutes</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>BareWire-level knob — not an SDK property.</b> This value controls BareWire's own
    /// manual background-renew loop that calls
    /// <c>ServiceBusSessionReceiver.RenewSessionLockAsync()</c> at intervals derived from
    /// <c>SessionReceiver.SessionLockedUntil</c> (approximately half the remaining lock window,
    /// with a ~10-second safety margin). It does <em>not</em> correspond to any
    /// <c>ServiceBusSessionReceiverOptions</c> property (none exists in SDK 7.20.1).
    /// </para>
    /// <para>
    /// Setting this to <see cref="TimeSpan.Zero"/> disables the background-renew entirely —
    /// only use this for very short sessions or if your lock duration exceeds the expected
    /// processing time. Must be <c>&gt;= <see cref="TimeSpan.Zero"/></c>; validated by
    /// <see cref="Validate"/>.
    /// </para>
    /// </remarks>
    public TimeSpan MaxAutoLockRenewDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Returns a diagnostic representation of these options with the <see cref="ConnectionString"/>
    /// redacted as <c>[Redacted]</c> to prevent accidental secret exposure in logs, exception
    /// messages, and diagnostic output (SEC-02/SEC-06).
    /// </summary>
    public override string ToString() =>
        $"AzureServiceBusTransportOptions {{ ConnectionString = [Redacted], PrefetchCount = {PrefetchCount}, " +
        $"MaxConcurrentCalls = {MaxConcurrentCalls}, EnableSessions = {EnableSessions}, " +
        $"MaxConcurrentSessions = {MaxConcurrentSessions}, " +
        $"SessionIdleTimeout = {SessionIdleTimeout?.ToString() ?? "null"}, " +
        $"MaxAutoLockRenewDuration = {MaxAutoLockRenewDuration} }}";

    /// <summary>
    /// Validates this options instance, throwing <see cref="BareWireConfigurationException"/>
    /// when required values are missing or invalid.
    /// </summary>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when <see cref="ConnectionString"/> is <see langword="null"/> or empty,
    /// <see cref="MaxConcurrentSessions"/> is less than 1,
    /// <see cref="SessionIdleTimeout"/> is set and not positive, or
    /// <see cref="MaxAutoLockRenewDuration"/> is negative.
    /// The exception message does not echo the connection string value (SEC-02).
    /// </exception>
    public void Validate()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            throw new BareWireConfigurationException(
                optionName: nameof(ConnectionString),
                optionValue: string.Empty,
                expectedValue: "A non-empty Azure Service Bus connection string " +
                               "(Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...)");
        }

        if (MaxConcurrentSessions < 1)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(MaxConcurrentSessions),
                optionValue: MaxConcurrentSessions.ToString(System.Globalization.CultureInfo.InvariantCulture),
                expectedValue: "An integer >= 1");
        }

        if (SessionIdleTimeout.HasValue && SessionIdleTimeout.Value <= TimeSpan.Zero)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(SessionIdleTimeout),
                optionValue: SessionIdleTimeout.Value.ToString(),
                expectedValue: "A positive TimeSpan (> 00:00:00)");
        }

        if (MaxAutoLockRenewDuration < TimeSpan.Zero)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(MaxAutoLockRenewDuration),
                optionValue: MaxAutoLockRenewDuration.ToString(),
                expectedValue: "A non-negative TimeSpan (>= 00:00:00)");
        }
    }
}
