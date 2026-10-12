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

    // Receive-stop handles of every registered consumer (session and non-session), resolved by SettleAsync
    // while a consumer is stopping.
    private readonly ConcurrentDictionary<string, IAzureServiceBusReceiveControl> _receiveControls =
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

    // Per-consumer count of settlements that are inside SettleAsync (entry already evicted, SDK call not yet
    // returned). Together with the in-flight count it tells a stopping consumer whether anything is still
    // being settled.
    private readonly ConcurrentDictionary<string, SettlementCounter> _settling =
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
        _receiveControls[consumerId] = consumer;
        _settling[consumerId] = new SettlementCounter();
        _messageMaps[consumerId] = new ConcurrentDictionary<ulong, (ServiceBusReceivedMessage, ServiceBusReceiver)>();
        _sessionIndex[consumerId] = new ConcurrentDictionary<string, ConcurrentDictionary<ulong, bool>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Registers a session consumer slot under the given <paramref name="consumerId"/>,
    /// creating a fresh empty message map and session index without adding to
    /// <see cref="AllConsumers"/> (session consumers are tracked separately by the adapter).
    /// </summary>
    /// <param name="consumerId">The session consumer id.</param>
    /// <param name="receiveControl">
    /// Optional receive-stop handle, so that <c>SettleAsync</c> can wait for the session receive loops to stop
    /// before it abandons a message while the consumer is stopping.
    /// </param>
    internal void RegisterSession(string consumerId, IAzureServiceBusReceiveControl? receiveControl = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(consumerId);

        if (receiveControl is not null)
        {
            _receiveControls[consumerId] = receiveControl;
        }

        _settling[consumerId] = new SettlementCounter();
        _messageMaps[consumerId] = new ConcurrentDictionary<ulong, (ServiceBusReceivedMessage, ServiceBusReceiver)>();
        _sessionIndex[consumerId] = new ConcurrentDictionary<string, ConcurrentDictionary<ulong, bool>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Removes the consumer and its message map. Safe to call when not registered.
    /// </summary>
    internal void Unregister(string consumerId)
    {
        _consumers.TryRemove(consumerId, out _);
        _receiveControls.TryRemove(consumerId, out _);
        _messageMaps.TryRemove(consumerId, out _);
        _sessionIndex.TryRemove(consumerId, out _);
        _settling.TryRemove(consumerId, out _);
    }

    /// <summary>
    /// Opens a settlement scope for <paramref name="consumerId"/>. It must be opened BEFORE the registry entry
    /// is evicted and disposed after the SDK settlement call returned, so that a stopping consumer never sees
    /// "nothing pending" while a settlement is still inside the SDK. Returns an inert scope when the consumer is
    /// not registered.
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
    /// registered in-flight entries plus settlements currently inside the SDK call.
    /// </summary>
    internal int PendingSettlementCount(string consumerId)
    {
        // Read order is load-bearing: in-flight FIRST, then settling. A settlement opens its scope (settling + 1)
        // before it evicts the entry (in-flight - 1), and closes the scope only after the SDK call returned.
        // Reading in-flight first means that a 0 observed there implies the eviction already happened, hence the
        // scope was already open and the later settling read still sees it. Reversing the two reads could
        // observe settling == 0 (before the scope opened) and then in-flight == 0 (after the eviction) and
        // report "nothing pending" while a settlement is about to call the SDK.
        int inFlight = InFlightCount(consumerId);
        int settling = _settling.TryGetValue(consumerId, out SettlementCounter? counter)
            ? Volatile.Read(ref counter.Value)
            : 0;
        return inFlight + settling;
    }

    /// <summary>
    /// Looks up the receive-stop handle of a registered consumer (session or non-session).
    /// </summary>
    internal bool TryGetReceiveControl(
        string consumerId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IAzureServiceBusReceiveControl? control) =>
        _receiveControls.TryGetValue(consumerId, out control);

    /// <summary>Gets the number of in-flight messages currently tracked for <paramref name="consumerId"/>.</summary>
    internal int InFlightCount(string consumerId) =>
        _messageMaps.TryGetValue(
            consumerId,
            out ConcurrentDictionary<ulong, (ServiceBusReceivedMessage, ServiceBusReceiver)>? map)
            ? map.Count
            : 0;

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
