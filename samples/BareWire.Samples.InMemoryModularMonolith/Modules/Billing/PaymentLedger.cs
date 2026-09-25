using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace BareWire.Samples.InMemoryModularMonolith.Modules.Billing;

/// <summary>
/// The Billing module's bounded in-memory state: which orders have had payment captured. Registered
/// as a singleton. Capacity is enforced with an <see cref="Interlocked"/> counter incremented before
/// the entry is added — never with <see cref="ConcurrentDictionary{TKey,TValue}.Count"/>, which is not
/// atomic with a concurrent add and would let concurrent callers race past the limit.
/// </summary>
/// <param name="logger">Logs capacity rejections.</param>
public sealed partial class PaymentLedger(ILogger<PaymentLedger> logger)
{
    /// <summary>The maximum number of distinct orders this ledger tracks.</summary>
    public const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, decimal> _captured = new();
    private int _count;

    /// <summary>
    /// Records that payment for <paramref name="orderId"/> has been captured. Idempotent — calling this
    /// again for an already-recorded order returns <see langword="true"/> without changing state, so a
    /// redelivered <c>OrderPlaced</c> (e.g. after an outbox retry) does not double-count against
    /// <see cref="MaxEntries"/>.
    /// </summary>
    /// <param name="orderId">The order identifier.</param>
    /// <param name="amount">The captured amount.</param>
    /// <returns>
    /// <see langword="true"/> when the order is recorded (now or previously); <see langword="false"/>
    /// when the ledger is at <see cref="MaxEntries"/> capacity and the order was rejected.
    /// </returns>
    public bool TryRecord(string orderId, decimal amount)
    {
        if (_captured.ContainsKey(orderId))
        {
            return true;
        }

        int updated = Interlocked.Increment(ref _count);
        if (updated > MaxEntries)
        {
            Interlocked.Decrement(ref _count);
            LogCapacityExceeded(logger, orderId);
            return false;
        }

        if (!_captured.TryAdd(orderId, amount))
        {
            // Lost the race to a concurrent TryRecord for the same order — it is already captured,
            // so give back the slot this call reserved.
            Interlocked.Decrement(ref _count);
        }

        return true;
    }

    /// <summary>Gets whether payment for <paramref name="orderId"/> has been captured.</summary>
    public bool IsCaptured(string orderId) => _captured.ContainsKey(orderId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Billing PaymentLedger capacity exceeded — order {OrderId} rejected")]
    private static partial void LogCapacityExceeded(ILogger logger, string orderId);
}
