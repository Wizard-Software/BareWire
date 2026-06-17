using BareWire.Abstractions.Exceptions;

namespace BareWire.Transport.AzureServiceBus;

/// <summary>
/// Configuration options for the Azure Service Bus transport adapter.
/// Apply via <see cref="ServiceCollectionExtensions.AddBareWireAzureServiceBus"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two authentication modes are supported — selected by <see cref="AuthMode"/>:
/// <list type="bullet">
/// <item>
/// <term><see cref="AzureServiceBusAuthMode.Sas"/> (default)</term>
/// <description>
/// Shared Access Signature via <see cref="ConnectionString"/>. Configure with
/// <c>UseSasAuth(connectionString)</c> or the legacy <c>ConnectionString(connectionString)</c>
/// method on the configurator.
/// </description>
/// </item>
/// <item>
/// <term><see cref="AzureServiceBusAuthMode.EntraId"/></term>
/// <description>
/// Azure Entra ID via <see cref="Credential"/> against <see cref="FullyQualifiedNamespace"/>.
/// Configure with <c>UseEntraIdAuth(fullyQualifiedNamespace, credential)</c> on the configurator.
/// Token refresh is handled automatically by the Azure SDK — BareWire does not implement its own
/// refresh loop.
/// </description>
/// </item>
/// </list>
/// Azure Service Bus uses AMQP-over-TLS (port 5671) by default — no credential is transmitted
/// in plaintext.
/// </para>
/// </remarks>
internal sealed class AzureServiceBusTransportOptions
{
    /// <summary>
    /// Gets or sets the authentication mode used to connect to the Azure Service Bus namespace.
    /// Defaults to <see cref="AzureServiceBusAuthMode.Sas"/> (connection-string SAS — R2.1 behaviour).
    /// </summary>
    /// <remarks>
    /// Set via the configurator methods:
    /// <list type="bullet">
    /// <item><c>UseSasAuth(connectionString)</c> — sets <see cref="AzureServiceBusAuthMode.Sas"/>.</item>
    /// <item><c>UseEntraIdAuth(fullyQualifiedNamespace, credential)</c> — sets <see cref="AzureServiceBusAuthMode.EntraId"/>.</item>
    /// </list>
    /// Token refresh in Entra ID mode is handled automatically by the Azure SDK
    /// (<c>Azure.Messaging.ServiceBus</c> 7.x); BareWire does not implement its own refresh loop.
    /// </remarks>
    public AzureServiceBusAuthMode AuthMode { get; set; } = AzureServiceBusAuthMode.Sas;

    /// <summary>
    /// Gets or sets the Service Bus connection string, including the SAS key.
    /// Format: <c>Endpoint=sb://&lt;namespace&gt;.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...</c>
    /// </summary>
    /// <remarks>
    /// <para>Used only when <see cref="AuthMode"/> is <see cref="AzureServiceBusAuthMode.Sas"/>.</para>
    /// <para>
    /// <b>Security (SEC-02/SEC-06):</b> This property contains a secret (<c>SharedAccessKey</c>).
    /// It is never logged, never included in <see cref="ToString"/>, and never echoed in exception
    /// messages. See <see cref="ToString"/> for the redacted representation.
    /// </para>
    /// </remarks>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the fully-qualified Azure Service Bus namespace host name.
    /// Format: <c>&lt;namespace&gt;.servicebus.windows.net</c>.
    /// </summary>
    /// <remarks>
    /// <para>Used only when <see cref="AuthMode"/> is <see cref="AzureServiceBusAuthMode.EntraId"/>.</para>
    /// <para>
    /// The namespace host is a non-secret identifier (it contains no key or token) and is safe
    /// to include in logs and diagnostic output.
    /// </para>
    /// </remarks>
    public string FullyQualifiedNamespace { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the <see cref="Azure.Core.TokenCredential"/> used to authenticate against
    /// the Azure Service Bus namespace.
    /// </summary>
    /// <remarks>
    /// <para>Used only when <see cref="AuthMode"/> is <see cref="AzureServiceBusAuthMode.EntraId"/>.</para>
    /// <para>
    /// <b>Security (SEC-02/SEC-06):</b> The credential object is never serialised, never logged,
    /// and never echoed in exception messages. <see cref="ToString"/> represents it by type name
    /// only (<c>Credential?.GetType().Name</c>). Token refresh is performed automatically by the
    /// Azure SDK (<c>Azure.Messaging.ServiceBus</c> 7.x); BareWire does not implement its own
    /// refresh loop.
    /// </para>
    /// </remarks>
    public Azure.Core.TokenCredential? Credential { get; set; }

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
    /// Returns a diagnostic representation of these options with secrets redacted to prevent
    /// accidental secret exposure in logs, exception messages, and diagnostic output (SEC-02/SEC-06).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="ConnectionString"/> is always shown as <c>[Redacted]</c>.</item>
    /// <item><see cref="Credential"/> is shown as its runtime type name (e.g. <c>DefaultAzureCredential</c>)
    /// or <c>null</c> — never the object itself.</item>
    /// <item><see cref="FullyQualifiedNamespace"/> is shown as-is — it is a non-secret host identifier.</item>
    /// </list>
    /// </remarks>
    public override string ToString() =>
        $"AzureServiceBusTransportOptions {{ AuthMode = {AuthMode}, ConnectionString = [Redacted], " +
        $"FullyQualifiedNamespace = {FullyQualifiedNamespace}, " +
        $"Credential = {Credential?.GetType().Name ?? "null"}, " +
        $"PrefetchCount = {PrefetchCount}, " +
        $"MaxConcurrentCalls = {MaxConcurrentCalls}, EnableSessions = {EnableSessions}, " +
        $"MaxConcurrentSessions = {MaxConcurrentSessions}, " +
        $"SessionIdleTimeout = {SessionIdleTimeout?.ToString() ?? "null"}, " +
        $"MaxAutoLockRenewDuration = {MaxAutoLockRenewDuration} }}";

    /// <summary>
    /// Validates this options instance, throwing <see cref="BareWireConfigurationException"/>
    /// when required values are missing or invalid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validation is mode-aware:
    /// <list type="bullet">
    /// <item>
    /// <see cref="AzureServiceBusAuthMode.Sas"/> — <see cref="ConnectionString"/> must be non-empty.
    /// </item>
    /// <item>
    /// <see cref="AzureServiceBusAuthMode.EntraId"/> — <see cref="FullyQualifiedNamespace"/> must
    /// be non-empty <b>and</b> <see cref="Credential"/> must not be <see langword="null"/>.
    /// <see cref="ConnectionString"/> is not validated and should be left empty.
    /// </item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when required values are missing or out of range. Exception messages never echo
    /// secrets (SEC-02): <see cref="ConnectionString"/> and <see cref="Credential"/> are never
    /// included in exception detail.
    /// </exception>
    public void Validate()
    {
        if (AuthMode == AzureServiceBusAuthMode.Sas)
        {
            if (string.IsNullOrEmpty(ConnectionString))
            {
                throw new BareWireConfigurationException(
                    optionName: nameof(ConnectionString),
                    optionValue: string.Empty,
                    expectedValue: "A non-empty Azure Service Bus connection string " +
                                   "(Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...)");
            }
        }
        else // AzureServiceBusAuthMode.EntraId
        {
            if (string.IsNullOrEmpty(FullyQualifiedNamespace))
            {
                throw new BareWireConfigurationException(
                    optionName: nameof(FullyQualifiedNamespace),
                    optionValue: string.Empty,
                    expectedValue: "A non-empty Azure Service Bus fully-qualified namespace host " +
                                   "(e.g. myns.servicebus.windows.net)");
            }

            if (Credential is null)
            {
                throw new BareWireConfigurationException(
                    optionName: nameof(Credential),
                    optionValue: string.Empty,
                    expectedValue: "A non-null Azure.Core.TokenCredential instance " +
                                   "(e.g. new DefaultAzureCredential())");
            }
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
