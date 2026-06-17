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
    /// Recommendation: keep <c>PrefetchCount &lt;= InternalQueueCapacity</c> and consider setting
    /// <c>MaxAutoLockRenewDuration</c> on the receiver to renew locks while messages wait in the
    /// bounded channel.
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

    /// <summary>
    /// Returns a diagnostic representation of these options with the <see cref="ConnectionString"/>
    /// redacted as <c>[Redacted]</c> to prevent accidental secret exposure in logs, exception
    /// messages, and diagnostic output (SEC-02/SEC-06).
    /// </summary>
    public override string ToString() =>
        $"AzureServiceBusTransportOptions {{ ConnectionString = [Redacted], PrefetchCount = {PrefetchCount}, MaxConcurrentCalls = {MaxConcurrentCalls} }}";

    /// <summary>
    /// Validates this options instance, throwing <see cref="BareWireConfigurationException"/>
    /// when required values are missing.
    /// </summary>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when <see cref="ConnectionString"/> is <see langword="null"/> or empty.
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
    }
}
