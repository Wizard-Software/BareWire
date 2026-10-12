using System.Collections.Concurrent;
using Confluent.Kafka;

namespace BareWire.Transport.Kafka.Internal;

/// <summary>
/// Thread-safe registry of active <see cref="KafkaConsumer"/> instances, keyed by consumer id.
/// Also exposes the per-consumer <c>DeliveryTag → TopicPartitionOffset</c> mapping used
/// by <c>SettleAsync</c> to commit the correct partition offset (D1).
/// </summary>
/// <remarks>
/// <para>
/// The registry is the authoritative source for resolving a consumer from an inbound message's
/// <c>BW-ConsumerId</c> header. This prevents spoofing: the header value is injected by the
/// consumer itself after <c>MapInbound</c>, using last-write-wins (D5), so a wire-level
/// <c>BW-ConsumerId</c> is always overwritten.
/// </para>
/// <para>
/// <b>Offset-map eviction (CLAUDE.md — no unbounded buffers):</b> entries are removed from
/// the per-consumer offset map on every settlement path (Ack and no-store), so the map
/// cannot grow without bound.
/// </para>
/// </remarks>
internal sealed class KafkaConsumerRegistry
{
    private readonly ConcurrentDictionary<string, KafkaConsumer> _consumers =
        new(StringComparer.Ordinal);

    // Per-consumer delivery-tag → TopicPartitionOffset map.
    // Outer key: consumerId; inner key: DeliveryTag (ulong).
    // Two-level to avoid cross-consumer DeliveryTag collisions (D1).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, TopicPartitionOffset>> _offsetMaps =
        new(StringComparer.Ordinal);

    // Per-consumer count of settlements that are inside SettleAsync (offset already evicted, work not yet
    // finished: retry/DLQ republish and the tracker update). Together with the in-flight count it tells a
    // stopping consumer whether anything is still being settled.
    private readonly ConcurrentDictionary<string, SettlementCounter> _settling =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Registers an active <paramref name="consumer"/> under the given <paramref name="consumerId"/>.
    /// Creates a fresh empty offset map for this consumer.
    /// </summary>
    /// <param name="consumerId">The unique consumer identifier (injected as <c>BW-ConsumerId</c>).</param>
    /// <param name="consumer">The consumer instance to register.</param>
    internal void Register(string consumerId, KafkaConsumer consumer)
    {
        ArgumentException.ThrowIfNullOrEmpty(consumerId);
        ArgumentNullException.ThrowIfNull(consumer);

        _consumers[consumerId] = consumer;
        _settling[consumerId] = new SettlementCounter();
        _offsetMaps[consumerId] = new ConcurrentDictionary<ulong, TopicPartitionOffset>();
    }

    /// <summary>
    /// Removes the consumer with the given <paramref name="consumerId"/> and its offset map
    /// from the registry. Safe to call if the consumer is not registered.
    /// </summary>
    /// <param name="consumerId">The consumer identifier to unregister.</param>
    internal void Unregister(string consumerId)
    {
        _consumers.TryRemove(consumerId, out _);
        _offsetMaps.TryRemove(consumerId, out _);
        _settling.TryRemove(consumerId, out _);
    }

    /// <summary>
    /// Opens a settlement scope for <paramref name="consumerId"/>. It must be opened BEFORE the offset entry is
    /// evicted and disposed only after the whole settlement finished (including a retry/DLQ republish and the
    /// commit-tracker update), so that a stopping consumer never sees "nothing pending" while a settlement is
    /// still running. Returns an inert scope when the consumer is not registered.
    /// </summary>
    internal SettlementScope BeginSettlement(string consumerId)
    {
        if (!_settling.TryGetValue(consumerId, out SettlementCounter? counter))
        {
            return default;
        }

        Interlocked.Increment(ref counter.Value);
        return new SettlementScope(counter);
    }

    /// <summary>
    /// Gets the number of messages of <paramref name="consumerId"/> that are received but not yet fully settled:
    /// registered in-flight offsets plus settlements currently running.
    /// </summary>
    internal int PendingSettlementCount(string consumerId)
    {
        // Read order is load-bearing: in-flight FIRST, then settling. A settlement opens its scope (settling + 1)
        // before it evicts the offset (in-flight - 1), and closes the scope only after it finished. Reading
        // in-flight first means that a 0 observed there implies the eviction already happened, hence the scope
        // was already open and the later settling read still sees it. Reversing the two reads could observe
        // settling == 0 (before the scope opened) and then in-flight == 0 (after the eviction) and report
        // "nothing pending" while a settlement is still about to run.
        int inFlight = _offsetMaps.TryGetValue(consumerId, out ConcurrentDictionary<ulong, TopicPartitionOffset>? map)
            ? map.Count
            : 0;
        int settling = _settling.TryGetValue(consumerId, out SettlementCounter? counter)
            ? Volatile.Read(ref counter.Value)
            : 0;
        return inFlight + settling;
    }

    /// <summary>
    /// Resolves the active consumer associated with the given <paramref name="consumerId"/>.
    /// Returns <see langword="null"/> when no consumer with that id is registered.
    /// </summary>
    /// <param name="consumerId">The <c>BW-ConsumerId</c> header value from the inbound message.</param>
    /// <returns>The matching <see cref="KafkaConsumer"/>, or <see langword="null"/>.</returns>
    internal KafkaConsumer? ResolveByConsumerId(string consumerId) =>
        _consumers.TryGetValue(consumerId, out KafkaConsumer? consumer) ? consumer : null;

    /// <summary>
    /// Returns a snapshot of all currently registered consumer ids.
    /// </summary>
    internal IReadOnlyCollection<string> ConsumerIds => (IReadOnlyCollection<string>)_consumers.Keys;

    /// <summary>
    /// Returns all registered consumers as a snapshot list. Used during <c>DisposeAsync</c>
    /// to stop all active consumers.
    /// </summary>
    internal IReadOnlyList<KafkaConsumer> AllConsumers() =>
        [.. _consumers.Values];

    // ── DeliveryTag → TopicPartitionOffset mapping ────────────────────────────

    /// <summary>
    /// Records a <see cref="TopicPartitionOffset"/> entry for the given consumer and delivery tag.
    /// Called by the polling loop immediately after stamping <c>InboundMessage.DeliveryTag</c>.
    /// </summary>
    /// <param name="consumerId">The consumer that received the message.</param>
    /// <param name="deliveryTag">The monotonic delivery tag assigned to this message (D1).</param>
    /// <param name="topicPartitionOffset">The Kafka topic/partition/offset for this message.</param>
    internal void StoreOffset(string consumerId, ulong deliveryTag, TopicPartitionOffset topicPartitionOffset)
    {
        if (_offsetMaps.TryGetValue(consumerId, out ConcurrentDictionary<ulong, TopicPartitionOffset>? map))
        {
            map[deliveryTag] = topicPartitionOffset;
        }
    }

    /// <summary>
    /// Retrieves and evicts the <see cref="TopicPartitionOffset"/> for the given consumer and delivery tag.
    /// Returns <see langword="null"/> when no entry exists (already evicted or unknown tag).
    /// </summary>
    /// <param name="consumerId">The consumer that received the message.</param>
    /// <param name="deliveryTag">The delivery tag whose offset to retrieve and evict.</param>
    /// <returns>The stored <see cref="TopicPartitionOffset"/>, or <see langword="null"/>.</returns>
    internal TopicPartitionOffset? TryEvictOffset(string consumerId, ulong deliveryTag)
    {
        if (_offsetMaps.TryGetValue(consumerId, out ConcurrentDictionary<ulong, TopicPartitionOffset>? map) &&
            map.TryRemove(deliveryTag, out TopicPartitionOffset? tpo))
        {
            return tpo;
        }

        return null;
    }

    /// <summary>Mutable count of settlements in progress for one consumer.</summary>
    internal sealed class SettlementCounter
    {
        internal int Value;
    }

    /// <summary>
    /// Marks one settlement as in progress until disposed. A struct, so opening a scope allocates nothing.
    /// </summary>
    internal readonly struct SettlementScope : IDisposable
    {
        private readonly SettlementCounter? _counter;

        internal SettlementScope(SettlementCounter? counter) => _counter = counter;

        /// <inheritdoc />
        public void Dispose()
        {
            if (_counter is not null)
            {
                Interlocked.Decrement(ref _counter.Value);
            }
        }
    }
}
