using BareWire.Abstractions.Transport;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// A delivery handed to a consumer and not settled yet, together with the queue whose reserved slot it
/// still holds and the runner that handed it out.
/// </summary>
/// <param name="Delivery">The delivery handed to the consumer. Read-only for every holder of this entry.</param>
/// <param name="Queue">The queue the delivery belongs to; its slot is released or reused on settlement.</param>
/// <param name="Owner">The runner that handed the delivery out.</param>
/// <param name="Message">
/// The inbound message the delivery was handed out in; it owns the delivery's buffer. Once it has been
/// disposed its pooled buffer is back in the pool and must no longer be read.
/// </param>
internal readonly record struct InFlightDelivery(
    InMemoryDelivery Delivery,
    InMemoryQueue Queue,
    InMemoryQueueRunner Owner,
    InboundMessage Message);
