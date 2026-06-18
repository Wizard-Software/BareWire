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
/// The registry is bounded by <see cref="_maxSize"/>: when <see cref="Count"/> reaches the limit,
/// <see cref="TryRegister"/> returns <see langword="false"/> and the caller must not push the
/// message into the consumer channel (PERF-3 mitigation).
/// </para>
/// </remarks>
internal sealed class PubSubInFlightRegistry
{
    private readonly ConcurrentDictionary<ulong, (string AckId, string SubscriptionName)> _entries = new();
    private readonly int _maxSize;

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
    /// Gets the current number of registered in-flight entries.
    /// </summary>
    internal int Count => _entries.Count;

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
        // Bounded check: reject registration when at or over capacity (PERF-3).
        // Note: Count is a snapshot — a small race window may temporarily exceed maxSize by a few,
        // but the capacity guard provides a strong probabilistic bound in practice.
        if (_entries.Count >= _maxSize)
        {
            return false;
        }

        return _entries.TryAdd(deliveryTag, (ackId, subscriptionName));
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
        return _entries.TryRemove(deliveryTag, out (string AckId, string SubscriptionName) entry)
            ? entry
            : null;
    }
}
