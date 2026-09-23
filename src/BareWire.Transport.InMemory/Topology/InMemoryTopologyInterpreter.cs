using System.Collections.Frozen;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.InMemory.Internal;

namespace BareWire.Transport.InMemory.Topology;

/// <summary>
/// Interprets a <see cref="TopologyDeclaration"/> (plus the receive-endpoint queue names and per-type
/// exchange mappings carried by <see cref="InMemoryTransportOptions"/>) into a sealed
/// <see cref="ExchangeRegistry"/>, failing fast on any configuration the in-memory transport cannot
/// support or that references an undeclared exchange or queue.
/// </summary>
/// <remarks>
/// Stateless by design — every member is a pure function over its arguments, so there is no static
/// mutable state to guard against concurrent calls.
/// </remarks>
internal static class InMemoryTopologyInterpreter
{
    /// <summary>The logical transport name used on every <see cref="BareWireTransportException"/> raised here.</summary>
    internal const string TransportName = "InMemory";

    /// <summary>
    /// Builds the sealed <see cref="ExchangeRegistry"/> for <paramref name="options"/>: validates
    /// exchange types and queue arguments, normalizes the topology, resolves receive-endpoint
    /// auto-declaration, and validates every binding, the default exchange, and every per-type exchange
    /// mapping against the declared exchanges and queues.
    /// </summary>
    /// <exception cref="BareWireTransportException">
    /// Thrown when an exchange is declared with <see cref="ExchangeType.Headers"/> or
    /// <see cref="ExchangeType.ConsistentHash"/>, or a queue declares an unsupported argument.
    /// </exception>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when the topology contains a conflicting duplicate, an empty exchange or queue name, a
    /// binding to an undeclared exchange or queue, an undeclared <see cref="InMemoryTransportOptions.DefaultExchange"/>,
    /// an undeclared exchange mapping, or a receive endpoint whose queue is neither declared nor
    /// eligible for auto-declaration.
    /// </exception>
    internal static ExchangeRegistry BuildRegistry(InMemoryTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        TopologyDeclaration topology = options.Topology ?? new TopologyDeclaration();

        ThrowIfUnsupportedExchangeTypes(topology);
        ThrowIfUnsupportedQueueArguments(topology);

        NormalizedTopology declared = Normalize(topology);

        // (d) Receive-endpoint queues — iterate UNIQUE queue names before touching declarations, so a
        // queue name repeated across two ReceiveEndpoint calls is resolved once, not twice.
        HashSet<string> uniqueEndpointQueueNames = new(StringComparer.Ordinal);
        foreach (var endpoint in options.EndpointConfigurations)
        {
            uniqueEndpointQueueNames.Add(endpoint.QueueName);
        }

        List<QueueDeclaration> autoDeclaredQueues = [];
        HashSet<string> autoDeclaredQueueNames = new(StringComparer.Ordinal);
        foreach (string endpointQueueName in uniqueEndpointQueueNames)
        {
            if (declared.Queues.ContainsKey(endpointQueueName))
            {
                // An explicit declaration always wins over auto-declaration.
                continue;
            }

            if (!options.AutoDeclareEndpointQueues)
            {
                throw new BareWireConfigurationException(
                    optionName: "ReceiveEndpoint",
                    optionValue: endpointQueueName,
                    expectedValue: "a queue declared via ConfigureTopology, or AutoDeclareEndpointQueues() enabled");
            }

            var autoQueue = new QueueDeclaration(endpointQueueName);
            autoDeclaredQueues.Add(autoQueue);
            autoDeclaredQueueNames.Add(endpointQueueName);
        }

        bool QueueExists(string name) => declared.Queues.ContainsKey(name) || autoDeclaredQueueNames.Contains(name);

        // (e) Bindings — exchange->queue requires a declared, non-default exchange and an existing
        // (declared or auto-declared) queue; exchange->exchange requires two declared, non-default
        // exchanges.
        foreach (ExchangeQueueBinding binding in declared.ExchangeQueueBindings)
        {
            if (binding.ExchangeName.Length == 0
                || !declared.Exchanges.ContainsKey(binding.ExchangeName)
                || !QueueExists(binding.QueueName))
            {
                throw new BareWireConfigurationException(
                    optionName: "ConfigureTopology.BindExchangeToQueue",
                    optionValue: $"{binding.ExchangeName} -> {binding.QueueName}",
                    expectedValue: "a declared exchange (not the default exchange \"\") and a declared queue");
            }
        }

        foreach (ExchangeExchangeBinding binding in declared.ExchangeExchangeBindings)
        {
            if (binding.SourceExchangeName.Length == 0
                || !declared.Exchanges.ContainsKey(binding.SourceExchangeName)
                || binding.DestinationExchangeName.Length == 0
                || !declared.Exchanges.ContainsKey(binding.DestinationExchangeName))
            {
                throw new BareWireConfigurationException(
                    optionName: "ConfigureTopology.BindExchangeToExchange",
                    optionValue: $"{binding.SourceExchangeName} -> {binding.DestinationExchangeName}",
                    expectedValue: "two declared exchanges (not the default exchange \"\")");
            }
        }

        // (f) Default exchange — null or "" selects the built-in default exchange; anything else must
        // be declared.
        if (options.DefaultExchange is { Length: > 0 } defaultExchange
            && !declared.Exchanges.ContainsKey(defaultExchange))
        {
            throw new BareWireConfigurationException(
                optionName: "DefaultExchange",
                optionValue: defaultExchange,
                expectedValue: "an exchange declared via ConfigureTopology, or an empty string for the default exchange");
        }

        // (g) Per-type exchange mappings — iterate in Type.FullName order so the exception (if any) is
        // deterministic regardless of dictionary enumeration order.
        foreach (KeyValuePair<Type, string> mapping in options.ExchangeMappings.OrderBy(
            static kvp => kvp.Key.FullName, StringComparer.Ordinal))
        {
            if (mapping.Value.Length > 0 && !declared.Exchanges.ContainsKey(mapping.Value))
            {
                throw new BareWireConfigurationException(
                    optionName: $"MapExchange<{mapping.Key.Name}>",
                    optionValue: mapping.Value,
                    expectedValue: "an exchange declared via ConfigureTopology");
            }
        }

        return new ExchangeRegistry(declared, autoDeclaredQueues);
    }

    /// <summary>
    /// Normalizes <paramref name="topology"/> into a <see cref="NormalizedTopology"/>: exchanges and
    /// queues are keyed by name, identical duplicate declarations are merged, conflicting duplicates
    /// throw, and bindings are deduplicated while preserving first-occurrence order.
    /// </summary>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown for an empty exchange or queue name, or for two declarations under the same name that
    /// disagree on type or flags (exchanges) or on flags or arguments (queues).
    /// </exception>
    internal static NormalizedTopology Normalize(TopologyDeclaration topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        Dictionary<string, ExchangeDeclaration> exchanges = new(StringComparer.Ordinal);
        foreach (ExchangeDeclaration declaration in topology.Exchanges)
        {
            if (declaration.Name.Length == 0)
            {
                throw new BareWireConfigurationException(
                    optionName: "ConfigureTopology.DeclareExchange",
                    optionValue: declaration.Name,
                    expectedValue: "a non-empty name (the default exchange \"\" is predeclared)");
            }

            if (exchanges.TryGetValue(declaration.Name, out ExchangeDeclaration? existing))
            {
                if (existing != declaration)
                {
                    throw new BareWireConfigurationException(
                        optionName: "ConfigureTopology.DeclareExchange",
                        optionValue: declaration.Name,
                        expectedValue: "a single declaration per exchange name (conflicting type or flags)");
                }
            }
            else
            {
                exchanges.Add(declaration.Name, declaration);
            }
        }

        Dictionary<string, QueueDeclaration> queues = new(StringComparer.Ordinal);
        foreach (QueueDeclaration declaration in topology.Queues)
        {
            if (declaration.Name.Length == 0)
            {
                throw new BareWireConfigurationException(
                    optionName: "ConfigureTopology.DeclareQueue",
                    optionValue: declaration.Name,
                    expectedValue: "a non-empty queue name");
            }

            // Copy the arguments now — the caller's source dictionary must not be able to mutate the
            // registry after BuildRegistry returns.
            QueueDeclaration copy = declaration with
            {
                Arguments = declaration.Arguments is { Count: > 0 }
                    ? declaration.Arguments.ToFrozenDictionary(StringComparer.Ordinal)
                    : FrozenDictionary<string, object>.Empty,
            };

            if (queues.TryGetValue(declaration.Name, out QueueDeclaration? existing))
            {
                if (!NormalizedTopology.QueueDeclarationsEqual(existing, copy))
                {
                    throw new BareWireConfigurationException(
                        optionName: "ConfigureTopology.DeclareQueue",
                        optionValue: declaration.Name,
                        expectedValue: "a single declaration per queue name (conflicting flags or arguments)");
                }
            }
            else
            {
                queues.Add(declaration.Name, copy);
            }
        }

        List<ExchangeQueueBinding> exchangeQueueBindings = [];
        HashSet<ExchangeQueueBinding> seenExchangeQueueBindings = [];
        foreach (ExchangeQueueBinding binding in topology.ExchangeQueueBindings)
        {
            if (seenExchangeQueueBindings.Add(binding))
            {
                exchangeQueueBindings.Add(binding);
            }
        }

        List<ExchangeExchangeBinding> exchangeExchangeBindings = [];
        HashSet<ExchangeExchangeBinding> seenExchangeExchangeBindings = [];
        foreach (ExchangeExchangeBinding binding in topology.ExchangeExchangeBindings)
        {
            if (seenExchangeExchangeBindings.Add(binding))
            {
                exchangeExchangeBindings.Add(binding);
            }
        }

        return new NormalizedTopology(exchanges, queues, exchangeQueueBindings, exchangeExchangeBindings);
    }

    /// <summary>
    /// Throws <see cref="BareWireTransportException"/> for every exchange declared with
    /// <see cref="ExchangeType.Headers"/> or <see cref="ExchangeType.ConsistentHash"/> — the only
    /// exchange types the in-memory router understands are <see cref="ExchangeType.Direct"/>,
    /// <see cref="ExchangeType.Fanout"/>, and <see cref="ExchangeType.Topic"/>.
    /// </summary>
    internal static void ThrowIfUnsupportedExchangeTypes(TopologyDeclaration topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        foreach (ExchangeDeclaration declaration in topology.Exchanges)
        {
            if (declaration.Type is ExchangeType.Headers or ExchangeType.ConsistentHash)
            {
                throw new BareWireTransportException(
                    $"Exchange '{declaration.Name}' is declared with type {declaration.Type}, which the " +
                    "in-memory transport does not support. Use Direct, Fanout or Topic.",
                    TransportName,
                    endpointAddress: null);
            }
        }
    }

    /// <summary>
    /// Throws <see cref="BareWireTransportException"/> for every queue argument the in-memory
    /// transport does not honour (<c>x-message-ttl</c>, <c>x-max-length</c>, <c>x-max-length-bytes</c>,
    /// <c>x-expires</c>). Every other argument (dead-letter routing, single-active-consumer, queue
    /// type, overflow strategy, or any unrecognized key) is accepted without interpretation.
    /// </summary>
    internal static void ThrowIfUnsupportedQueueArguments(TopologyDeclaration topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        foreach (QueueDeclaration declaration in topology.Queues)
        {
            if (declaration.Arguments is null)
            {
                continue;
            }

            foreach (string key in declaration.Arguments.Keys)
            {
                if (IsUnsupportedQueueArgumentKey(key))
                {
                    throw new BareWireTransportException(
                        $"Queue '{declaration.Name}' declares the argument '{key}', which the in-memory " +
                        "transport does not support.",
                        TransportName,
                        endpointAddress: null);
                }
            }
        }
    }

    // Queue arguments the in-memory transport does not honour. Declaring one is rejected outright
    // rather than silently ignored, to avoid a queue that behaves differently in-memory than on a
    // broker transport with no observable warning. A pattern match rather than a static lookup
    // collection, per the assembly's "no mutable static state" rule.
    private static bool IsUnsupportedQueueArgumentKey(string key) =>
        key is "x-message-ttl" or "x-max-length" or "x-max-length-bytes" or "x-expires";
}
