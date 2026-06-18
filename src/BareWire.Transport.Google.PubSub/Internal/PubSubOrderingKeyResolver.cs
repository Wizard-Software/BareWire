namespace BareWire.Transport.Google.PubSub.Internal;

/// <summary>
/// Pure, broker-free decision logic for Google Cloud Pub/Sub ordering key resolution.
/// Resolves the ordering key from BareWire outbound headers using a priority ladder,
/// mirroring the pattern established by <c>SqsFifoMapper</c> (ADR-015) and
/// <c>AzureServiceBusSessionMapper</c> (ADR-011).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering key resolution order:</b>
/// <list type="number">
/// <item><description>
///   <c>BW-OrderingKey</c> (<see cref="PubSubHeaderMapper.OrderingKeyHeader"/>) —
///   explicit producer override; passed through to <c>PubsubMessage.OrderingKey</c> as-is.
/// </description></item>
/// <item><description>
///   <c>correlation-id</c> (<see cref="CorrelationIdHeader"/>, kebab-case, as stamped by
///   <c>BareWireBus</c> on the send path — see <c>BareWireBus.cs:451-453</c>) —
///   automatic per-saga-instance ordering without requiring producer awareness.
/// </description></item>
/// <item><description>
///   <see cref="string.Empty"/> — no ordering key resolved; Pub/Sub publishes the message
///   without ordering (safe when <c>EnableMessageOrdering</c> is <see langword="false"/>).
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Resolution lives here rather than inline in <c>SendBatchAsync</c> for the same reason
/// <c>SqsFifoMapper</c> exists: the logic is fully unit-testable without a broker and is
/// identical in structure to the sibling transports.
/// </para>
/// </remarks>
internal static class PubSubOrderingKeyResolver
{
    /// <summary>
    /// BareWire canonical header for the Pub/Sub ordering key (priority 1).
    /// Mirrors <see cref="PubSubHeaderMapper.OrderingKeyHeader"/> — exposed here so test code
    /// can reference it without importing the parent namespace explicitly.
    /// </summary>
    internal const string OrderingKeyHeaderName = PubSubHeaderMapper.OrderingKeyHeader;

    /// <summary>
    /// Canonical correlation-id header name (kebab-case) stamped by <c>BareWireBus</c>
    /// on the send/saga path (<c>BareWireBus.cs:451-453</c>). Used as the priority-2 fallback
    /// for automatic per-saga ordering. Matches <c>SqsHeaderMapper.CorrelationIdHeader</c>
    /// for cross-transport consistency.
    /// </summary>
    internal const string CorrelationIdHeader = "correlation-id";

    /// <summary>
    /// Resolves the Pub/Sub ordering key from <paramref name="headers"/> using the priority ladder:
    /// <c>BW-OrderingKey</c> → <c>correlation-id</c> → <see cref="string.Empty"/>.
    /// </summary>
    /// <param name="headers">The outbound BareWire headers dictionary. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// The resolved ordering key, or <see cref="string.Empty"/> when no ordering key could be determined.
    /// An empty result means Pub/Sub will publish without message ordering for this message.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="headers"/> is <see langword="null"/>.
    /// </exception>
    internal static string Resolve(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        // Priority 1: explicit BW-OrderingKey header.
        if (headers.TryGetValue(PubSubHeaderMapper.OrderingKeyHeader, out string? explicitKey)
            && !string.IsNullOrEmpty(explicitKey))
        {
            return explicitKey;
        }

        // Priority 2: correlation-id fallback (kebab-case — mirrors BareWireBus.cs:451-453).
        if (headers.TryGetValue(CorrelationIdHeader, out string? correlationId)
            && !string.IsNullOrEmpty(correlationId))
        {
            return correlationId;
        }

        return string.Empty;
    }
}
