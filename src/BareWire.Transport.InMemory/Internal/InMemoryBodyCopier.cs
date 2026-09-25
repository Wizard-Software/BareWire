using BareWire.Abstractions.Transport;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The one place the in-memory transport copies a delivery's body out of its
/// <see cref="InboundMessage"/> while a consumer may be disposing that message concurrently. Shared by
/// settlement (requeue and dead-letter copies, in <see cref="InMemorySettlement"/>) and
/// <see cref="InMemoryQueueRunner"/>'s own requeue-on-abandon path, so the pin/rent/copy/unpin sequence
/// is implemented exactly once.
/// </summary>
internal static class InMemoryBodyCopier
{
    /// <summary>
    /// Copies <c>entry.Delivery</c>'s body into a freshly rented buffer.
    /// </summary>
    /// <remarks>
    /// Pins FIRST — nothing is rented at all when the message was already disposed, so a disposed message
    /// never costs a rent-and-immediately-return round trip. Once pinned, the rent and the copy both run
    /// inside a <c>try</c> whose <c>finally</c> always unpins — including if <see cref="InMemoryBufferPool.Rent"/>
    /// itself were to throw — pairing every successful <see cref="InboundMessage.TryPinPooledBuffer"/> with
    /// exactly one <see cref="InboundMessage.UnpinPooledBuffer"/>, so the message is never left pinned.
    /// </remarks>
    /// <param name="entry">The in-flight entry whose message body is copied.</param>
    /// <param name="pool">The pool the copy is rented from.</param>
    /// <param name="copy">
    /// The rented, filled copy on success; an empty array (nothing rented) when this call returns
    /// <see langword="false"/>.
    /// </param>
    /// <returns>
    /// <see langword="false"/> — with no rent performed — when <c>entry.Message</c> was already disposed
    /// by the consumer before this call could pin it, so its body is no longer readable.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <see langword="null"/>.</exception>
    internal static bool TryCopy(InFlightDelivery entry, InMemoryBufferPool pool, out byte[] copy)
    {
        ArgumentNullException.ThrowIfNull(pool);

        if (!entry.Message.TryPinPooledBuffer())
        {
            copy = [];
            return false;
        }

        InMemoryDelivery delivery = entry.Delivery;
        byte[] rented;
        try
        {
            rented = pool.Rent(delivery.Length);
            delivery.Body.Span.CopyTo(rented);
        }
        finally
        {
            entry.Message.UnpinPooledBuffer();
        }

        copy = rented;
        return true;
    }
}
