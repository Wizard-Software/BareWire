using BareWire.Abstractions;
using BareWire.Abstractions.Topology;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Resolves the consumer queues reachable from a <see cref="ExchangeType.Fanout"/> or
/// <see cref="ExchangeType.Topic"/> exchange in a sealed <see cref="ExchangeRegistry"/> — the queues at
/// risk of receiving a duplicate delivery when a transactional outbox retries a message that some of
/// them already accepted.
/// </summary>
/// <remarks>
/// Stateless by design — the single member is a pure function over its arguments, so there is no static
/// mutable state to guard against concurrent calls.
/// </remarks>
internal static class FanOutQueueResolver
{
    /// <summary>
    /// Returns the consumer queues reachable from a <see cref="ExchangeType.Fanout"/> or
    /// <see cref="ExchangeType.Topic"/> exchange, directly or through one or more exchange-to-exchange
    /// bindings. Deduplicated and sorted with <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    /// <param name="registry">The sealed topology registry to inspect.</param>
    /// <param name="consumerQueueNames">
    /// The names of the queues that have at least one consumer — only these are eligible for the result,
    /// even when a fan-out exchange also reaches a queue with no consumer.
    /// </param>
    /// <returns>
    /// The consumer queue names reachable from a fan-out exchange closure, deduplicated and sorted with
    /// <see cref="StringComparer.Ordinal"/>. Empty when no fan-out exchange reaches a consumer queue.
    /// </returns>
    internal static IReadOnlyList<string> FindConsumerQueues(
        ExchangeRegistry registry,
        IEnumerable<string> consumerQueueNames)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(consumerQueueNames);

        var consumerQueues = new HashSet<string>(consumerQueueNames, StringComparer.Ordinal);
        if (consumerQueues.Count == 0)
        {
            return [];
        }

        HashSet<string> fanOutExchanges = SeedFanOutExchanges(registry);
        ExpandThroughExchangeBindings(registry, fanOutExchanges);

        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (ExchangeQueueBinding binding in registry.ExchangeQueueBindings)
        {
            if (fanOutExchanges.Contains(binding.ExchangeName) && consumerQueues.Contains(binding.QueueName))
            {
                result.Add(binding.QueueName);
            }
        }

        return [.. result];
    }

    private static HashSet<string> SeedFanOutExchanges(ExchangeRegistry registry)
    {
        var fanOutExchanges = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExchangeDeclaration exchange in registry.Exchanges.Values)
        {
            if (exchange.Type is ExchangeType.Fanout or ExchangeType.Topic)
            {
                fanOutExchanges.Add(exchange.Name);
            }
        }

        return fanOutExchanges;
    }

    /// <summary>
    /// Grows <paramref name="fanOutExchanges"/> to its closure under exchange-to-exchange bindings via
    /// breadth-first search. The only termination condition is <see cref="HashSet{T}.Add"/> returning
    /// <see langword="false"/> for every newly-visited destination, so a binding cycle cannot loop
    /// forever — each exchange is enqueued at most once.
    /// </summary>
    private static void ExpandThroughExchangeBindings(ExchangeRegistry registry, HashSet<string> fanOutExchanges)
    {
        var bindingsBySource = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (ExchangeExchangeBinding binding in registry.ExchangeExchangeBindings)
        {
            if (!bindingsBySource.TryGetValue(binding.SourceExchangeName, out List<string>? destinations))
            {
                destinations = [];
                bindingsBySource[binding.SourceExchangeName] = destinations;
            }

            destinations.Add(binding.DestinationExchangeName);
        }

        var frontier = new Queue<string>(fanOutExchanges);
        while (frontier.Count > 0)
        {
            string source = frontier.Dequeue();
            if (!bindingsBySource.TryGetValue(source, out List<string>? destinations))
            {
                continue;
            }

            foreach (string destination in destinations)
            {
                if (fanOutExchanges.Add(destination))
                {
                    frontier.Enqueue(destination);
                }
            }
        }
    }
}
