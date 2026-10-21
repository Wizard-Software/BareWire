using System.Diagnostics;
using BareWire.Abstractions.Transport;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The one place the in-memory transport copies a delivery's body out of its
/// <see cref="InboundMessage"/> while a consumer may be disposing that message concurrently. Shared by
/// settlement (requeue and dead-letter copies, in <see cref="InMemorySettlement"/>) and
/// <see cref="InMemoryQueueRunner"/>'s own requeue-on-abandon path, so the rent/copy sequence is
/// implemented once (<see cref="CopyPinned"/>) and the pin handling once per ownership model:
/// <see cref="TryCopy"/> pins on its own behalf, while the runner pins before it claims the map entry and
/// calls <see cref="CopyPinned"/> directly.
/// </summary>
internal static class InMemoryBodyCopier
{
    // Spins before TryCopy starts sleeping 1 ms between pin attempts. A pin is normally held for the few
    // microseconds of a body copy, so the first spins (which SpinWait already turns into yields) cover it;
    // the sleep only matters when the pin holder was preempted on a loaded machine, where spinning on the
    // same core would starve it. 10 keeps the common case spin-only without burning a core for long.
    private const int SleepThreshold = 10;

    // Safeguard against a pin holder that never unpins (a leak): without it the retry loop would never
    // end. Far above any legitimate pin duration; reaching it is treated as "not copyable", as before.
    private const double PinWaitLimitMilliseconds = 5000;

    /// <summary>
    /// Copies <c>entry.Delivery</c>'s body into a freshly rented buffer, pinning the message for the
    /// duration of the copy.
    /// </summary>
    /// <remarks>
    /// Pins FIRST — nothing is rented at all when the message was already disposed, so a disposed message
    /// never costs a rent-and-immediately-return round trip. A pin that fails while the message still
    /// holds its pooled buffer means another holder (the runner's cleanup) currently has the pin: a dispose
    /// in the pinned state does not return the buffer, only the matching unpin does, so the buffer outlives
    /// that holder and this call retries until the pin succeeds or the message is truly disposed (its
    /// buffer is then <see langword="null"/>). Only a message that never had a pooled buffer, or a pin
    /// holder that never unpins, ends the wait early. The fast path is a single pin attempt.
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

        InboundMessage message = entry.Message;
        if (!message.TryPinPooledBuffer() && !TryPinWhileBufferExists(message))
        {
            copy = [];
            return false;
        }

        try
        {
            copy = CopyPinned(entry, pool);
        }
        finally
        {
            message.UnpinPooledBuffer();
        }

        return true;
    }

    /// <summary>
    /// Copies <c>entry.Delivery</c>'s body into a freshly rented buffer; the caller already holds the pin.
    /// </summary>
    /// <remarks>
    /// The caller must hold a pin taken with <see cref="InboundMessage.TryPinPooledBuffer"/> on
    /// <c>entry.Message</c> and releases it itself with <see cref="InboundMessage.UnpinPooledBuffer"/>
    /// — typically in a <c>finally</c>, so that every successful pin is paired with exactly one unpin
    /// even if <see cref="InMemoryBufferPool.Rent"/> throws. This method neither pins nor unpins.
    /// </remarks>
    /// <param name="entry">The in-flight entry whose message body is copied.</param>
    /// <param name="pool">The pool the copy is rented from.</param>
    /// <returns>The rented, filled copy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <see langword="null"/>.</exception>
    internal static byte[] CopyPinned(InFlightDelivery entry, InMemoryBufferPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);

        InMemoryDelivery delivery = entry.Delivery;
        byte[] rented = pool.Rent(delivery.Length);
        delivery.Body.Span.CopyTo(rented);
        return rented;
    }

    // Retries the pin while the message still owns its pooled buffer: the buffer is released (set to null)
    // only by a dispose that found no pin, or by the unpin that follows a dispose during a pin. So a
    // non-null buffer with a failed pin means "pinned by someone who will unpin" (or a dispose racing to
    // null the buffer), and the loop ends as soon as the holder is done.
    private static bool TryPinWhileBufferExists(InboundMessage message)
    {
        var spinner = new SpinWait();
        long start = Stopwatch.GetTimestamp();
        while (message.PooledBuffer is not null)
        {
            spinner.SpinOnce(SleepThreshold);
            if (message.TryPinPooledBuffer())
            {
                return true;
            }

            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= PinWaitLimitMilliseconds)
            {
                return false;
            }
        }

        return false;
    }
}
