namespace BareWire.Abstractions.Transport;

/// <summary>
/// Allows the consumer pipeline to release a consumer channel after all in-flight
/// settlements for that channel are complete.
/// Implemented by transport adapters that maintain per-consumer channel state.
/// </summary>
/// <remarks>
/// This is an internal coordination protocol between <c>BareWire.Core</c> and transport adapters.
/// It is not part of the public <see cref="ITransportAdapter"/> surface.
/// <para>
/// After a <see cref="ITransportAdapter.ConsumeAsync"/> iteration ends, the transport deliberately
/// keeps the channel open so callers can still settle (ACK/NACK) in-flight messages via
/// <see cref="ITransportAdapter.SettleAsync"/>. Once all settlements are complete, the consumer
/// pipeline calls <see cref="ReleaseConsumerChannelAsync"/> to close and discard the channel.
/// </para>
/// </remarks>
internal interface IConsumerChannelManager
{
    /// <summary>
    /// Releases and closes the consumer channel identified by <paramref name="channelId"/>.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    /// <param name="channelId">
    /// The unique consumer channel identifier stamped in the <c>BW-ConsumerChannelId</c>
    /// header on every <see cref="InboundMessage"/> produced by the consumer.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the close operation.</param>
    /// <returns>A <see cref="Task"/> that completes when the channel has been closed and disposed.</returns>
    Task ReleaseConsumerChannelAsync(string channelId, CancellationToken cancellationToken = default);
}
