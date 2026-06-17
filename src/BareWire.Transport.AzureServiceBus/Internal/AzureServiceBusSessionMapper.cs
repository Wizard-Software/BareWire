namespace BareWire.Transport.AzureServiceBus.Internal;

/// <summary>
/// Pure, broker-free decision logic that resolves the Azure Service Bus <c>SessionId</c>
/// to stamp on a <c>ServiceBusMessage</c> from a set of BareWire outbound headers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolution order (D-1/D-13):</b>
/// <list type="number">
/// <item><description><c>BW-SessionId</c> header — explicit override.</description></item>
/// <item><description><c>correlation-id</c> header (kebab-case, as populated by <c>BareWireBus</c>) — automatic per-saga-instance FIFO via <c>ISagaState.CorrelationId</c>.</description></item>
/// <item><description><see langword="null"/> — no SessionId set; non-session queue behaviour is preserved (R2.1 backward-compatible).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Casing note (D-13/GAP-5):</b> The fallback key is kebab-case <c>"correlation-id"</c>,
/// matching how the bus populates the header. PascalCase <c>"CorrelationId"</c> is never written
/// by <c>BareWireBus</c> and would be a dead fallback.
/// </para>
/// </remarks>
internal static class AzureServiceBusSessionMapper
{
    /// <summary>
    /// Resolves the Azure Service Bus <c>SessionId</c> from <paramref name="headers"/>.
    /// Returns <see langword="null"/> when neither <c>BW-SessionId</c> nor <c>correlation-id</c>
    /// is present and non-empty — the caller should leave <c>ServiceBusMessage.SessionId</c> unset.
    /// </summary>
    /// <param name="headers">The outbound BareWire headers dictionary. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// The resolved session id, or <see langword="null"/> when no session should be assigned.
    /// </returns>
    internal static string? Resolve(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        // Priority 1: explicit BW-SessionId header.
        if (headers.TryGetValue(AzureServiceBusHeaderMapper.SessionIdHeader, out string? sessionId) &&
            !string.IsNullOrEmpty(sessionId))
        {
            return sessionId;
        }

        // Priority 2: correlation-id fallback (kebab-case — D-13/GAP-5).
        if (headers.TryGetValue(AzureServiceBusHeaderMapper.CorrelationIdHeader, out string? correlationId) &&
            !string.IsNullOrEmpty(correlationId))
        {
            return correlationId;
        }

        return null;
    }
}
