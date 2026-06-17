namespace BareWire.Abstractions.Transport;

/// <summary>
/// Optional capability interface implemented by transport adapters whose broker supports
/// native scheduled delivery (e.g. Azure Service Bus <c>ScheduleMessageAsync</c>).
/// </summary>
/// <remarks>
/// <para>
/// This interface is intentionally separate from <see cref="ITransportAdapter"/> so that
/// transports which do not support native scheduling are not forced to implement it.
/// Consumers probe the capability at runtime via the pattern
/// <c>transport as INativeMessageScheduler</c>, mirroring how
/// <see cref="BareWire.Abstractions.TransportCapabilities.NativeScheduling"/> is advertised
/// through <see cref="ITransportAdapter.Capabilities"/>.
/// </para>
/// <para>
/// Living in <c>BareWire.Abstractions</c> allows <c>BareWire.Saga</c> to consume the
/// capability WITHOUT referencing any transport project, preserving the layer boundary
/// enforced by NetArchTest Rule 5.
/// </para>
/// </remarks>
public interface INativeMessageScheduler
{
    /// <summary>
    /// Schedules <paramref name="message"/> for native broker delivery at the specified
    /// <paramref name="scheduledEnqueueTime"/>. Returns a transport-neutral
    /// <see cref="ScheduledMessageToken"/> sufficient to cancel the message later.
    /// </summary>
    /// <param name="message">The outbound message to schedule.</param>
    /// <param name="scheduledEnqueueTime">
    /// The UTC time at which the broker should enqueue the message for delivery.
    /// </param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// A <see cref="ScheduledMessageToken"/> carrying the broker sequence number and the
    /// destination, both needed by <see cref="CancelScheduledAsync"/>.
    /// </returns>
    Task<ScheduledMessageToken> ScheduleAsync(
        OutboundMessage message,
        DateTimeOffset scheduledEnqueueTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a previously scheduled message identified by <paramref name="token"/>.
    /// The token carries the destination so the adapter can resolve the correct sender
    /// without additional state.
    /// </summary>
    /// <param name="token">
    /// The token returned by <see cref="ScheduleAsync"/> when the message was scheduled.
    /// </param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    Task CancelScheduledAsync(
        ScheduledMessageToken token,
        CancellationToken cancellationToken = default);
}
