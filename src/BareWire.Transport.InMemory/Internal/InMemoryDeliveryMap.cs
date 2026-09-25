using System.Collections.Concurrent;
using BareWire.Abstractions.Transport;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The map of deliveries handed to consumers and not settled yet, keyed by a delivery tag that is unique
/// within one <see cref="InMemoryTransportAdapter"/>. Settlement receives only the inbound message, so its
/// delivery tag is the sole handle back to the delivery, its queue, and the runner that handed it out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded.</b> Every entry belongs to a delivery that still holds a reserved slot of its queue, so the
/// number of entries never exceeds the sum of the capacities of the broker's queues — the bound comes from
/// the queues' admission counters, not from a separate limit here.
/// </para>
/// <para>
/// <b>Single winner.</b> Every path that ends an entry (settlement, a runner's cleanup, the adapter's
/// shutdown sweep) removes it with an atomic remove; exactly one of them wins, so the queue slot is
/// released or reused exactly once.
/// </para>
/// </remarks>
internal sealed class InMemoryDeliveryMap
{
    private readonly ConcurrentDictionary<ulong, InFlightDelivery> _entries = new();
    private long _lastTag;

    /// <summary>
    /// Gets the number of deliveries currently in flight. A test and diagnostic hook only: counting locks
    /// every segment of the underlying dictionary, so no production path (a drain included) may poll it —
    /// drains rely on the queues' occupancy instead.
    /// </summary>
    internal int Count => _entries.Count;

    /// <summary>Reserves a new delivery tag, never reused within this map.</summary>
    /// <returns>A delivery tag greater than zero.</returns>
    internal ulong NextTag() => (ulong)Interlocked.Increment(ref _lastTag);

    /// <summary>Registers a delivery handed to a consumer under a tag obtained from <see cref="NextTag"/>.</summary>
    /// <param name="deliveryTag">The tag reserved for this delivery.</param>
    /// <param name="entry">The in-flight entry.</param>
    /// <exception cref="InvalidOperationException">The tag is already registered.</exception>
    internal void Add(ulong deliveryTag, InFlightDelivery entry)
    {
        if (!_entries.TryAdd(deliveryTag, entry))
        {
            throw new InvalidOperationException($"Delivery tag {deliveryTag} is already in flight.");
        }
    }

    /// <summary>
    /// Removes and returns the entry for <paramref name="deliveryTag"/>. Returns <see langword="false"/>
    /// when the tag is unknown or another path has already removed it.
    /// </summary>
    internal bool TryTake(ulong deliveryTag, out InFlightDelivery entry) => _entries.TryRemove(deliveryTag, out entry);

    /// <summary>
    /// Returns, without removing them, the entries handed out by <paramref name="owner"/>, ordered by
    /// delivery tag (the order in which they were handed out). The caller claims each one with
    /// <see cref="TryTake"/>; an entry settled concurrently is simply lost to the caller.
    /// </summary>
    internal List<KeyValuePair<ulong, InFlightDelivery>> SnapshotOwnedBy(InMemoryQueueRunner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var owned = new List<KeyValuePair<ulong, InFlightDelivery>>();
        foreach (KeyValuePair<ulong, InFlightDelivery> entry in _entries)
        {
            if (ReferenceEquals(entry.Value.Owner, owner))
            {
                owned.Add(entry);
            }
        }

        owned.Sort(static (x, y) => x.Key.CompareTo(y.Key));
        return owned;
    }

    /// <summary>
    /// Removes and returns every entry still present, ordered by delivery tag. Entries removed
    /// concurrently by another path are not returned.
    /// </summary>
    internal List<InFlightDelivery> TakeAll()
    {
        var taken = new List<KeyValuePair<ulong, InFlightDelivery>>();
        foreach (ulong tag in _entries.Keys)
        {
            if (_entries.TryRemove(tag, out InFlightDelivery entry))
            {
                taken.Add(new KeyValuePair<ulong, InFlightDelivery>(tag, entry));
            }
        }

        taken.Sort(static (x, y) => x.Key.CompareTo(y.Key));
        return taken.ConvertAll(static pair => pair.Value);
    }

    /// <summary>
    /// Removes and returns every entry whose message was disposed by its consumer without being settled
    /// — <see cref="InFlightDelivery.Message"/>'s <see cref="InboundMessage.PooledBuffer"/> is already
    /// <see langword="null"/>, so its body can no longer be read or requeued. A non-invasive probe: a
    /// runner's own cleanup (<see cref="InMemoryQueueRunner"/>) only claims such an entry once its
    /// enumeration ends, so this method is what lets a drain notice — and reclaim the queue slot of — a
    /// delivery abandoned mid-enumeration, on every polling iteration rather than only at the end.
    /// </summary>
    /// <returns>
    /// The claimed entries, in no particular order. The result list is allocated lazily — only once the
    /// first match is found — so a call that claims nothing (the common case: most polling iterations of
    /// a drain find nothing released this way) costs one dictionary scan and never allocates a list with
    /// capacity for anything.
    /// </returns>
    internal List<InFlightDelivery> TakeReleasedByConsumer()
    {
        List<InFlightDelivery>? released = null;
        foreach (KeyValuePair<ulong, InFlightDelivery> candidate in _entries)
        {
            if (candidate.Value.Message.PooledBuffer is not null)
            {
                continue;
            }

            if (_entries.TryRemove(candidate.Key, out InFlightDelivery entry))
            {
                (released ??= []).Add(entry);
            }
        }

        return released ?? [];
    }
}
