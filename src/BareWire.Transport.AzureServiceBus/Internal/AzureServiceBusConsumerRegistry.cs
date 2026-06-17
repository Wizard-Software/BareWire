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
/// </remarks>
internal sealed class AzureServiceBusConsumerRegistry
{
    private readonly ConcurrentDictionary<string, AzureServiceBusConsumer> _consumers =
        new(StringComparer.Ordinal);

    // Per-consumer delivery-tag → (message, receiver) map.
    // Two-level to avoid cross-consumer DeliveryTag collisions (D-2).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, (ServiceBusReceivedMessage Message, ServiceBusReceiver Receiver)>> _messageMaps =
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
    }

    /// <summary>
    /// Removes the consumer and its message map. Safe to call when not registered.
    /// </summary>
    internal void Unregister(string consumerId)
    {
        _consumers.TryRemove(consumerId, out _);
        _messageMaps.TryRemove(consumerId, out _);
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
    internal void StoreMessage(
        string consumerId,
        ulong deliveryTag,
        ServiceBusReceivedMessage message,
        ServiceBusReceiver receiver)
    {
        if (_messageMaps.TryGetValue(
            consumerId,
            out ConcurrentDictionary<ulong, (ServiceBusReceivedMessage, ServiceBusReceiver)>? map))
        {
            map[deliveryTag] = (message, receiver);
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
}
