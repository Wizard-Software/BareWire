using System.Collections.Concurrent;

namespace BareWire.Transport.Google.PubSub.Internal;

/// <summary>
/// Thread-safe registry that maps a monotonic <c>DeliveryTag</c> (<see cref="ulong"/>) to
/// the corresponding Pub/Sub <c>AckId</c> and <c>SubscriptionName</c> needed to settle the message.
/// </summary>
/// <remarks>
/// <para>
/// Registration is performed on the consume path immediately after the message is pushed into the
/// bounded channel. Eviction is performed exactly once on the settlement path via
/// <see cref="TryEvict"/> — subsequent calls for the same tag return <see langword="null"/>
/// (evict-once semantics prevent double-settle and unbounded registry growth).
/// </para>
/// <para>
/// The registry is bounded by <see cref="_maxSize"/>. Consumers reserve slots atomically with
/// <see cref="TryReserve"/> before calling the broker, so concurrent endpoints can never hold more than
/// the limit in total; <see cref="TryRegister"/> reserves and registers a single entry in one step.
/// </para>
/// </remarks>
internal sealed class PubSubInFlightRegistry
{
    private readonly ConcurrentDictionary<ulong, (string AckId, string SubscriptionName)> _entries = new();
    private readonly int _maxSize;

    // Reservations plus registered entries; the only source of Count. Every slot is claimed with a CAS
    // so concurrent consumers can never hold more than _maxSize slots in total.
    private int _occupied;

    // Number of ConsumeAsync polling loops currently running; the divisor of the fair share.
    private int _activeConsumers;

    /// <summary>
    /// Initializes a new <see cref="PubSubInFlightRegistry"/> with the specified maximum size.
    /// </summary>
    /// <param name="maxSize">
    /// The maximum number of concurrent in-flight entries. Must be at least 1.
    /// </param>
    internal PubSubInFlightRegistry(int maxSize)
    {
        if (maxSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSize), maxSize, "maxSize must be at least 1.");
        }

        _maxSize = maxSize;
    }

    /// <summary>
    /// Gets the current number of occupied slots: registered in-flight entries plus outstanding reservations.
    /// </summary>
    internal int Count => Volatile.Read(ref _occupied);

    /// <summary>
    /// Gets the number of consumer polling loops currently registered via <see cref="EnterConsumer"/>.
    /// </summary>
    internal int ActiveConsumers => Volatile.Read(ref _activeConsumers);

    /// <summary>
    /// Registers a starting consumer polling loop. Must be paired with <see cref="ExitConsumer"/>.
    /// </summary>
    internal void EnterConsumer() => Interlocked.Increment(ref _activeConsumers);

    /// <summary>
    /// Unregisters a stopping consumer polling loop; the counter never drops below zero.
    /// </summary>
    internal void ExitConsumer()
    {
        while (true)
        {
            int current = Volatile.Read(ref _activeConsumers);
            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _activeConsumers, current - 1, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Caps <paramref name="requested"/> to this consumer's fair share of the registry limit:
    /// <c>Min(requested, Max(1, limit / activeConsumers))</c>. A single consumer keeps the full limit.
    /// </summary>
    /// <param name="requested">The number of slots the consumer wants.</param>
    /// <returns>The capped request; zero or negative requests are returned as zero.</returns>
    internal int GetFairShare(int requested)
    {
        if (requested <= 0)
        {
            return 0;
        }

        int consumers = Math.Max(1, Volatile.Read(ref _activeConsumers));
        return Math.Min(requested, Math.Max(1, _maxSize / consumers));
    }

    /// <summary>
    /// Atomically reserves up to <paramref name="requested"/> free slots before a broker call.
    /// </summary>
    /// <param name="requested">The number of slots wanted.</param>
    /// <returns>The number of slots granted, between 0 and <paramref name="requested"/>.</returns>
    internal int TryReserve(int requested)
    {
        if (requested <= 0)
        {
            return 0;
        }

        while (true)
        {
            int current = Volatile.Read(ref _occupied);
            int granted = Math.Min(requested, _maxSize - current);
            if (granted <= 0)
            {
                return 0;
            }

            if (Interlocked.CompareExchange(ref _occupied, current + granted, current) == current)
            {
                return granted;
            }
        }
    }

    /// <summary>
    /// Returns reservations that were not converted into entries.
    /// </summary>
    /// <param name="count">The number of unused reserved slots; zero is a no-op.</param>
    internal void ReleaseReservations(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _occupied, -count);
        }
    }

    /// <summary>
    /// Converts one reservation into a registered entry. The caller must hold a reservation.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when registered; <see langword="false"/> when the tag already exists, in which
    /// case the reservation stays with the caller.
    /// </returns>
    internal bool RegisterReserved(ulong deliveryTag, string ackId, string subscriptionName)
        => _entries.TryAdd(deliveryTag, (ackId, subscriptionName));

    /// <summary>
    /// Attempts to register a new in-flight entry.
    /// </summary>
    /// <param name="deliveryTag">The monotonic delivery tag assigned to the message.</param>
    /// <param name="ackId">The Pub/Sub ack ID used to settle the message.</param>
    /// <param name="subscriptionName">The fully-qualified subscription name from which the message was received.</param>
    /// <returns>
    /// <see langword="true"/> when the entry was registered successfully;
    /// <see langword="false"/> when the registry is at capacity (PERF-3: bounded size).
    /// </returns>
    internal bool TryRegister(ulong deliveryTag, string ackId, string subscriptionName)
    {
        // Reserve-and-register through the same counter as the consumer loop, so both paths share one limit.
        if (TryReserve(1) == 0)
        {
            return false;
        }

        if (_entries.TryAdd(deliveryTag, (ackId, subscriptionName)))
        {
            return true;
        }

        ReleaseReservations(1);
        return false;
    }

    /// <summary>
    /// Evicts the entry for the given <paramref name="deliveryTag"/> exactly once and returns it.
    /// </summary>
    /// <param name="deliveryTag">The delivery tag to evict.</param>
    /// <returns>
    /// The registered <c>(AckId, SubscriptionName)</c> tuple when found and evicted;
    /// <see langword="null"/> on miss or when the entry was already evicted (evict-once).
    /// </returns>
    internal (string AckId, string SubscriptionName)? TryEvict(ulong deliveryTag)
    {
        if (_entries.TryRemove(deliveryTag, out (string AckId, string SubscriptionName) entry))
        {
            Interlocked.Decrement(ref _occupied);
            return entry;
        }

        return null;
    }
}
