using BareWire.Abstractions.Transport;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Observes the moment <see cref="InMemoryQueueRunner"/>'s requeue-on-abandon path has claimed an
/// unsettled delivery from the delivery map and is about to copy its body.
/// </summary>
/// <remarks>
/// A test hook that opens the window in which a consumer can settle (a no-op by then) and dispose the
/// very message being requeued. Production code never supplies an implementation — the runner accepts
/// <see langword="null"/> and pays exactly one null check per claimed delivery when none is supplied.
/// </remarks>
internal interface IInMemoryRequeueClaimObserver
{
    /// <summary>
    /// Called on the cleanup thread right after the delivery was taken from the map and before its body
    /// is copied.
    /// </summary>
    /// <param name="message">The inbound message the claimed delivery was handed out in.</param>
    void OnRequeueClaimed(InboundMessage message);
}
