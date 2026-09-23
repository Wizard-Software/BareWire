namespace BareWire.Abstractions.Transport;

/// <summary>
/// Allows the bus to let in-flight work finish during a graceful shutdown by waiting until the
/// transport's queues with an active consumer have been drained, before consumer loops are cancelled.
/// Implemented by transport adapters that buffer accepted messages in process (for example the
/// in-memory transport), where cancelling consumers first would silently drop accepted messages.
/// </summary>
/// <remarks>
/// This is an internal coordination protocol between <c>BareWire</c> (the bus control) and transport
/// adapters. It is not part of the public <see cref="ITransportAdapter"/> surface, in the same way
/// that <see cref="IConsumerChannelManager"/> and <see cref="IDurableParkSettlement"/> are not
/// public — transports backed by a durable broker are not required to implement this interface.
/// <para>
/// The drain is bounded: it completes when every accepted-message counter of a queue with an active
/// consumer reaches zero, when <c>timeout</c> elapses, or when <c>cancellationToken</c> is
/// cancelled — whichever happens first. Queues without an active consumer (including dead-letter
/// queues) are skipped, because nothing would ever drain them.
/// </para>
/// </remarks>
internal interface IGracefulDrainTransport
{
    /// <summary>
    /// Waits until the accepted-message counters of all queues with an active consumer drop to zero,
    /// or until <paramref name="timeout"/> elapses or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the drain to complete.</param>
    /// <param name="cancellationToken">A token that ends the wait early.</param>
    /// <returns>
    /// A task that completes when the drain finished, the timeout elapsed, or the wait was cancelled.
    /// Messages still queued after the timeout are not delivered (at-most-once).
    /// </returns>
    Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
