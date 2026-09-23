using System.Collections.Frozen;
using BareWire.Abstractions.Topology;

namespace BareWire.Transport.InMemory.Topology;

/// <summary>
/// An immutable, structurally comparable snapshot of a <see cref="TopologyDeclaration"/>: exchanges
/// and queues keyed by name (duplicate identical declarations merged, conflicting duplicates rejected
/// upstream by <see cref="InMemoryTopologyInterpreter.Normalize"/>), with exchange-to-queue and
/// exchange-to-exchange bindings deduplicated while preserving first-occurrence order.
/// </summary>
internal sealed class NormalizedTopology
{
    internal NormalizedTopology(
        IReadOnlyDictionary<string, ExchangeDeclaration> exchanges,
        IReadOnlyDictionary<string, QueueDeclaration> queues,
        IReadOnlyList<ExchangeQueueBinding> exchangeQueueBindings,
        IReadOnlyList<ExchangeExchangeBinding> exchangeExchangeBindings)
    {
        ArgumentNullException.ThrowIfNull(exchanges);
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(exchangeQueueBindings);
        ArgumentNullException.ThrowIfNull(exchangeExchangeBindings);

        Exchanges = exchanges;
        Queues = queues;
        ExchangeQueueBindings = exchangeQueueBindings;
        ExchangeExchangeBindings = exchangeExchangeBindings;
    }

    /// <summary>Gets the exchange declarations keyed by name (<see cref="StringComparer.Ordinal"/>).</summary>
    internal IReadOnlyDictionary<string, ExchangeDeclaration> Exchanges { get; }

    /// <summary>Gets the queue declarations keyed by name (<see cref="StringComparer.Ordinal"/>).</summary>
    internal IReadOnlyDictionary<string, QueueDeclaration> Queues { get; }

    /// <summary>Gets the deduplicated exchange-to-queue bindings, in first-occurrence order.</summary>
    internal IReadOnlyList<ExchangeQueueBinding> ExchangeQueueBindings { get; }

    /// <summary>Gets the deduplicated exchange-to-exchange bindings, in first-occurrence order.</summary>
    internal IReadOnlyList<ExchangeExchangeBinding> ExchangeExchangeBindings { get; }

    /// <summary>
    /// Determines whether this topology and <paramref name="other"/> declare the same exchanges,
    /// queues, and bindings, independent of declaration order or duplicate entries.
    /// </summary>
    internal bool IsEquivalentTo(NormalizedTopology other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Exchanges.Count != other.Exchanges.Count || Queues.Count != other.Queues.Count)
        {
            return false;
        }

        foreach ((string name, ExchangeDeclaration declaration) in Exchanges)
        {
            if (!other.Exchanges.TryGetValue(name, out ExchangeDeclaration? otherDeclaration)
                || declaration != otherDeclaration)
            {
                return false;
            }
        }

        foreach ((string name, QueueDeclaration declaration) in Queues)
        {
            if (!other.Queues.TryGetValue(name, out QueueDeclaration? otherDeclaration)
                || !QueueDeclarationsEqual(declaration, otherDeclaration))
            {
                return false;
            }
        }

        return new HashSet<ExchangeQueueBinding>(ExchangeQueueBindings).SetEquals(other.ExchangeQueueBindings)
            && new HashSet<ExchangeExchangeBinding>(ExchangeExchangeBindings).SetEquals(other.ExchangeExchangeBindings);
    }

    /// <summary>
    /// Compares two <see cref="QueueDeclaration"/> instances for structural equality, including their
    /// <see cref="QueueDeclaration.Arguments"/>.
    /// </summary>
    /// <remarks>
    /// Argument values are compared with <see cref="object.Equals(object?, object?)"/>: the runtime type
    /// of the value must match exactly (an <see langword="int"/> and a <see langword="long"/> holding the
    /// same number are NOT equal), and a collection-valued argument is compared by reference, not by
    /// content. A <see langword="null"/> argument dictionary is treated as equivalent to an empty one.
    /// </remarks>
    internal static bool QueueDeclarationsEqual(QueueDeclaration left, QueueDeclaration right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            || left.Durable != right.Durable
            || left.Exclusive != right.Exclusive
            || left.AutoDelete != right.AutoDelete)
        {
            return false;
        }

        IReadOnlyDictionary<string, object> leftArguments = left.Arguments ?? FrozenDictionary<string, object>.Empty;
        IReadOnlyDictionary<string, object> rightArguments = right.Arguments ?? FrozenDictionary<string, object>.Empty;

        if (leftArguments.Count != rightArguments.Count)
        {
            return false;
        }

        foreach ((string key, object value) in leftArguments)
        {
            if (!rightArguments.TryGetValue(key, out object? otherValue) || !Equals(value, otherValue))
            {
                return false;
            }
        }

        return true;
    }
}
