using System.Text;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;

namespace BareWire.Transport.RabbitMQ.Internal;

/// <summary>
/// Stateless calculator that derives a topology-change-detection signal (the "mapping epoch")
/// from a <see cref="TopologyDeclaration"/>.
///
/// <para>
/// The epoch is a deterministic FNV-1a 64-bit hash of the sorted set of queue names that are
/// bound to at least one <see cref="ExchangeType.ConsistentHash"/> exchange in the topology.
/// Two instances given identical topology declarations will always produce the same epoch value,
/// enabling cross-instance consistency without any shared state or broker round-trips.
/// </para>
///
/// <para>
/// A <see langword="null"/> return value means "no stamp should be applied" — this occurs when
/// the topology is <see langword="null"/>, when there are no consistent-hash exchanges, or when
/// no queues are bound to a consistent-hash exchange.
/// </para>
/// </summary>
internal static class MappingEpochCalculator
{
    /// <summary>
    /// Computes the mapping epoch for the given topology.
    /// </summary>
    /// <param name="topology">
    /// The topology declaration to derive the epoch from, or <see langword="null"/> if no
    /// topology has been configured.
    /// </param>
    /// <returns>
    /// A deterministic <see cref="long"/> epoch derived from the sorted set of queue names
    /// bound to consistent-hash exchanges; or <see langword="null"/> when no stamp should
    /// be applied (topology is null, no consistent-hash exchange exists, or no bound queues).
    /// </returns>
    internal static long? Compute(TopologyDeclaration? topology)
    {
        if (topology is null)
        {
            return null;
        }

        HashSet<string> consistentHashExchanges = topology.Exchanges
            .Where(e => e.Type == ExchangeType.ConsistentHash)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (consistentHashExchanges.Count == 0)
        {
            return null;
        }

        string[] boundQueues = topology.ExchangeQueueBindings
            .Where(b => consistentHashExchanges.Contains(b.ExchangeName))
            .Select(b => b.QueueName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(q => q, StringComparer.Ordinal)
            .ToArray();

        if (boundQueues.Length == 0)
        {
            return null;
        }

        // FNV-1a 64-bit over UTF-8 bytes of queue names joined by '\n'.
        // Deterministic across processes and instances — string.GetHashCode() is explicitly
        // forbidden here because it is process-local and not stable across restarts.
        return Fnv1a64(string.Join('\n', boundQueues));
    }

    private static long Fnv1a64(string s)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offset;

        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= prime;
        }

        return unchecked((long)hash);
    }
}
