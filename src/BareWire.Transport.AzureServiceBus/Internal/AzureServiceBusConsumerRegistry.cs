using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;

namespace BareWire.Transport.AzureServiceBus.Internal;

/// <summary>
/// Thread-safe registry of active <see cref="AzureServiceBusConsumer"/> instances and the
/// per-message <c>DeliveryTag → (ServiceBusReceivedMessage, ServiceBusReceiver)</c> mapping
/// used by <c>SettleAsync</c> to execute the correct PeekLock settlement operation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Message retention invariant (R-4):</b> A <see cref="ServiceBusReceivedMessage"/> must
/// remain registered in this map until <c>SettleAsync</c> consumes it. The message body is
/// wrapped zero-copy via <c>BinaryData.ToMemory()</c>, which references the SDK's internal
/// buffer; the receiver holds the lock until settlement. Evicting the entry before settlement
/// would not affect the body wrap, but it would prevent <c>SettleAsync</c> from finding the
/// receiver — causing a <c>BareWireTransportException</c>.
/// </para>
/// <para>
/// <b>No unbounded buffers (CONSTITUTION):</b> Entries are evicted exactly once in
/// <c>SettleAsync</c>. The consumer loop's bounded channel provides the backpressure that
/// prevents the in-flight message count from growing without bound.
/// </para>
/// <para>
/// <b>Session support (R2.2 / D-11):</b> When a session is released or the session lock is
/// lost, <see cref="EvictAllForSession"/> removes all in-flight entries for that session in
/// bulk, preventing unbounded registry growth under session churn.
/// <see cref="ServiceBusSessionReceiver"/> inherits from <see cref="ServiceBusReceiver"/>, so
/// settlement methods work polymorphically — no separate settlement path is needed.
/// </para>
/// </remarks>
internal sealed class AzureServiceBusConsumerRegistry
{
    private readonly ConcurrentDictionary<string, AzureServiceBusConsumer> _consumers =
        new(StringComparer.Ordinal);

    // Per-consumer delivery-tag → (message, receiver) map.
    // Two-level to avoid cross-consumer DeliveryTag collisions (D-2).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, (ServiceBusReceivedMessage Message, ServiceBusReceiver Receiver)>> _messageMaps =
        new(StringComparer.Ordinal);

    // Per-consumer, per-session → set of delivery tags.
    // Third-level index for O(group) bulk eviction (D-11/VER-2).
    // Key: consumerId → (sessionId → ConcurrentDictionary<deliveryTag, bool>)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<ulong, bool>>> _sessionIndex =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Registers an active <paramref name="consumer"/> under the given <paramref name="consumerId"/>
    /// and creates a fresh empty message map for this consumer.
    /// </summary>
    internal void Register(string consumerId, AzureServiceBusConsumer consumer)
    {
        ArgumentException.ThrowIfNullOrEmpty(consumerId);
        ArgumentNullException.ThrowIfNull(consumer);

        _consumers[consumerId] = consumer;
        _messageMaps[consumerId] = new ConcurrentDictionary<ulong, (ServiceBusReceivedMessage, ServiceBusReceiver)>();
        _sessionIndex[consumerId] = new ConcurrentDictionary<string, ConcurrentDictionary<ulong, bool>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Registers a session consumer slot under the given <paramref name="consumerId"/>,
    /// creating a fresh empty message map and session index without adding to
    /// <see cref="AllConsumers"/> (session consumers are tracked separately by the adapter).
    /// </summary>
    internal void RegisterSession(string consumerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(consumerId);

        _messageMaps[consumerId] = new ConcurrentDictionary<ulong, (ServiceBusReceivedMessage, ServiceBusReceiver)>();
        _sessionIndex[consumerId] = new ConcurrentDictionary<string, ConcurrentDictionary<ulong, bool>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Removes the consumer and its message map. Safe to call when not registered.
    /// </summary>
    internal void Unregister(string consumerId)
    {
        _consumers.TryRemove(consumerId, out _);
        _messageMaps.TryRemove(consumerId, out _);
        _sessionIndex.TryRemove(consumerId, out _);
    }

    /// <summary>
    /// Returns all registered consumers as a snapshot list.
    /// Used during <c>DisposeAsync</c> to stop all active consumers.
    /// </summary>
    internal IReadOnlyList<AzureServiceBusConsumer> AllConsumers() =>
        [.. _consumers.Values];

    // ── Per-message DeliveryTag tracking ──────────────────────────────────────

    /// <summary>
    /// Records the <see cref="ServiceBusReceivedMessage"/> and its owning
    /// <see cref="ServiceBusReceiver"/> for the given consumer and delivery tag.
    /// Called by the polling loop immediately after stamping <c>InboundMessage.DeliveryTag</c>.
    /// </summary>
    /// <param name="consumerId">The consumer that received the message.</param>
    /// <param name="deliveryTag">The per-consumer monotonic delivery tag.</param>
    /// <param name="message">The received message to retain for settlement.</param>
    /// <param name="receiver">The receiver that holds the PeekLock.</param>
    /// <param name="sessionId">
    /// Optional session id. When non-null, registers the tag in the per-session index
    /// for O(group) bulk eviction via <see cref="EvictAllForSession"/>.
    /// </param>
    internal void StoreMessage(
        string consumerId,
        ulong deliveryTag,
        ServiceBusReceivedMessage message,
        ServiceBusReceiver receiver,
        string? sessionId = null)
    {
        if (_messageMaps.TryGetValue(
            consumerId,
            out ConcurrentDictionary<ulong, (ServiceBusReceivedMessage, ServiceBusReceiver)>? map))
        {
            map[deliveryTag] = (message, receiver);
        }

        // Register in session index for bulk eviction (D-11).
        if (sessionId is not null &&
            _sessionIndex.TryGetValue(
                consumerId,
                out ConcurrentDictionary<string, ConcurrentDictionary<ulong, bool>>? consumerIndex))
        {
            ConcurrentDictionary<ulong, bool> tagSet = consumerIndex.GetOrAdd(
                sessionId,
                _ => new ConcurrentDictionary<ulong, bool>());
            tagSet[deliveryTag] = true;
        }
    }

    /// <summary>
    /// Retrieves and evicts the <see cref="ServiceBusReceivedMessage"/> and
    /// <see cref="ServiceBusReceiver"/> for the given consumer and delivery tag.
    /// Returns <see langword="null"/> when no entry exists (already evicted or unknown tag).
    /// </summary>
    internal (ServiceBusReceivedMessage Message, ServiceBusReceiver Receiver)? TryEvictMessage(
        string consumerId,
        ulong deliveryTag)
    {
        if (_messageMaps.TryGetValue(
            consumerId,
            out ConcurrentDictionary<ulong, (ServiceBusReceivedMessage, ServiceBusReceiver)>? map) &&
            map.TryRemove(deliveryTag, out (ServiceBusReceivedMessage Message, ServiceBusReceiver Receiver) entry))
        {
            return entry;
        }

        return null;
    }

    // ── Session bulk eviction (D-11 / VER-2 / R2.2) ──────────────────────────

    /// <summary>
    /// Removes all delivery-tag entries associated with the given <paramref name="sessionId"/>
    /// for the specified <paramref name="consumerId"/>. Called in the session-consumer
    /// <c>finally</c> block when a session is released or the session lock is lost.
    /// </summary>
    /// <remarks>
    /// Idempotent — safe to call when the session or consumer is not registered.
    /// After this method returns, no entries for the session remain in <c>_messageMaps</c>
    /// or <c>_sessionIndex</c>.
    /// </remarks>
    /// <param name="consumerId">The consumer that owned the session.</param>
    /// <param name="sessionId">The session whose entries to evict.</param>
    internal void EvictAllForSession(string consumerId, string sessionId)
    {
        if (!_sessionIndex.TryGetValue(
            consumerId,
            out ConcurrentDictionary<string, ConcurrentDictionary<ulong, bool>>? consumerIndex))
        {
            return;
        }

        if (!consumerIndex.TryRemove(sessionId, out ConcurrentDictionary<ulong, bool>? tagSet))
        {
            return;
        }

        if (!_messageMaps.TryGetValue(
            consumerId,
            out ConcurrentDictionary<ulong, (ServiceBusReceivedMessage, ServiceBusReceiver)>? map))
        {
            return;
        }

        // Bulk-remove all delivery tags belonging to this session.
        foreach (ulong deliveryTag in tagSet.Keys)
        {
            map.TryRemove(deliveryTag, out _);
        }
    }
}
