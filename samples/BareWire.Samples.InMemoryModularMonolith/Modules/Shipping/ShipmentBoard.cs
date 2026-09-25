using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace BareWire.Samples.InMemoryModularMonolith.Modules.Shipping;

/// <summary>The Shipping module's view of an order's readiness. See <see cref="ShipmentBoard"/>.</summary>
public enum ShipmentStatus
{
    /// <summary>The order was received (fanout <c>OrderPlaced</c>) but payment is not yet confirmed.</summary>
    OrderReceived,

    /// <summary>Payment was confirmed (topic <c>PaymentCaptured</c>) but the order was not yet received.</summary>
    PaymentConfirmed,

    /// <summary>Both signals arrived — the order is ready to ship.</summary>
    ReadyToShip,
}

/// <summary>
/// The Shipping module's bounded in-memory state, tracking two independent signals per order —
/// <c>OrderPlaced</c> (fanout) and <c>PaymentCaptured</c> (topic) — which can arrive in either order.
/// Registered as a singleton. Capacity is enforced with an <see cref="Interlocked"/> counter
/// incremented before a new entry is created — never with
/// <see cref="ConcurrentDictionary{TKey,TValue}.Count"/>. Marking an existing entry's second flag does
/// not consume additional capacity.
/// </summary>
/// <param name="logger">Logs capacity rejections.</param>
public sealed partial class ShipmentBoard(ILogger<ShipmentBoard> logger)
{
    /// <summary>The maximum number of distinct orders this board tracks.</summary>
    public const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, ShipmentEntry> _entries = new();
    private int _count;

    /// <summary>Marks the order as received (fanout <c>OrderPlaced</c> consumed).</summary>
    public void MarkOrderReceived(string orderId) => GetOrCreateEntry(orderId)?.MarkOrderReceived();

    /// <summary>Marks the order's payment as confirmed (topic <c>PaymentCaptured</c> consumed).</summary>
    public void MarkPaymentConfirmed(string orderId) => GetOrCreateEntry(orderId)?.MarkPaymentConfirmed();

    /// <summary>
    /// Gets the current status of <paramref name="orderId"/>, or <see langword="null"/> when the order
    /// is unknown to this module (neither signal has arrived).
    /// </summary>
    public ShipmentStatus? GetStatus(string orderId)
    {
        if (!_entries.TryGetValue(orderId, out ShipmentEntry? entry))
        {
            return null;
        }

        if (entry.IsReadyToShip)
        {
            return ShipmentStatus.ReadyToShip;
        }

        if (entry.IsPaymentConfirmed)
        {
            return ShipmentStatus.PaymentConfirmed;
        }

        if (entry.IsOrderReceived)
        {
            return ShipmentStatus.OrderReceived;
        }

        return null;
    }

    private ShipmentEntry? GetOrCreateEntry(string orderId)
    {
        if (_entries.TryGetValue(orderId, out ShipmentEntry? existing))
        {
            return existing;
        }

        int updated = Interlocked.Increment(ref _count);
        if (updated > MaxEntries)
        {
            Interlocked.Decrement(ref _count);
            LogCapacityExceeded(logger, orderId);
            return null;
        }

        var created = new ShipmentEntry();
        ShipmentEntry entry = _entries.GetOrAdd(orderId, created);
        if (!ReferenceEquals(entry, created))
        {
            // Lost the race to a concurrent GetOrCreateEntry for the same order — give back the slot
            // this call reserved; the winning call's slot already accounts for this order.
            Interlocked.Decrement(ref _count);
        }

        return entry;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shipping ShipmentBoard capacity exceeded — order {OrderId} rejected")]
    private static partial void LogCapacityExceeded(ILogger logger, string orderId);

    /// <summary>Two independent, atomically-settable signals for one order.</summary>
    private sealed class ShipmentEntry
    {
        private int _orderReceived;
        private int _paymentConfirmed;

        public void MarkOrderReceived() => Interlocked.Exchange(ref _orderReceived, 1);

        public void MarkPaymentConfirmed() => Interlocked.Exchange(ref _paymentConfirmed, 1);

        public bool IsOrderReceived => Volatile.Read(ref _orderReceived) == 1;

        public bool IsPaymentConfirmed => Volatile.Read(ref _paymentConfirmed) == 1;

        public bool IsReadyToShip => IsOrderReceived && IsPaymentConfirmed;
    }
}
