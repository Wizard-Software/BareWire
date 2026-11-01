namespace BareWire.Abstractions.Serialization;

/// <summary>
/// Internal capability of the bus send endpoint: sends a pre-serialized payload and lets the caller supply the
/// <see cref="IOutboundHeaderContributor"/> that adds wire-format headers (for example <c>MT-Activity-Id</c>)
/// after the trace context has been injected. Kept internal so no public <see cref="ISendEndpoint"/> member is added.
/// </summary>
internal interface IContributingSendEndpoint
{
    /// <summary>
    /// Sends <paramref name="payload"/> as-is, applying <paramref name="headerContributor"/> (when not
    /// <see langword="null"/>) to the outbound headers of a traced send.
    /// </summary>
    Task SendRawAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        IOutboundHeaderContributor? headerContributor,
        CancellationToken cancellationToken = default);
}
