using System.Collections.Frozen;
using BareWire.Abstractions.Topology;
using BareWire.Transport.InMemory.Topology;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// An immutable, sealed snapshot of the in-memory transport's topology: every declared exchange and
/// queue, every queue auto-declared for a receive endpoint, and every exchange-to-queue and
/// exchange-to-exchange binding. Built once by <see cref="InMemoryTopologyInterpreter.BuildRegistry"/>
/// when the transport adapter is constructed and never mutated afterwards.
/// </summary>
/// <remarks>
/// The default exchange (an empty name) is predeclared: <see cref="ContainsExchange"/> returns
/// <see langword="true"/> for an empty name, but <see cref="Exchanges"/> never contains an entry keyed
/// by the empty string — it is a routing shortcut, not a declared exchange.
/// </remarks>
internal sealed class ExchangeRegistry
{
    /// <summary>The name of the built-in default exchange (routing directly to a queue by name).</summary>
    internal const string DefaultExchangeName = "";

    internal ExchangeRegistry(NormalizedTopology declared, IReadOnlyCollection<QueueDeclaration> autoDeclaredQueues)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(autoDeclaredQueues);

        Declared = declared;
        Exchanges = declared.Exchanges.ToFrozenDictionary(StringComparer.Ordinal);

        Dictionary<string, QueueDeclaration> queues = new(declared.Queues, StringComparer.Ordinal);
        foreach (QueueDeclaration autoQueue in autoDeclaredQueues)
        {
            queues[autoQueue.Name] = autoQueue;
        }

        Queues = queues.ToFrozenDictionary(StringComparer.Ordinal);
        AutoDeclaredQueueNames = autoDeclaredQueues.Select(static q => q.Name).ToFrozenSet(StringComparer.Ordinal);
        ExchangeQueueBindings = declared.ExchangeQueueBindings;
        ExchangeExchangeBindings = declared.ExchangeExchangeBindings;
    }

    /// <summary>
    /// Gets the explicit topology declaration this registry was built from — the auto-declared
    /// receive-endpoint queues are NOT included, so this is the same shape <c>DeployTopologyAsync</c>
    /// compares against when checking whether a redeployment is identical to the sealed topology.
    /// </summary>
    internal NormalizedTopology Declared { get; }

    /// <summary>Gets the declared exchanges, keyed by name. Never contains the default exchange ("").</summary>
    internal FrozenDictionary<string, ExchangeDeclaration> Exchanges { get; }

    /// <summary>Gets every queue — explicitly declared or auto-declared for a receive endpoint — keyed by name.</summary>
    internal FrozenDictionary<string, QueueDeclaration> Queues { get; }

    /// <summary>Gets the names of the queues that were auto-declared for a receive endpoint.</summary>
    internal FrozenSet<string> AutoDeclaredQueueNames { get; }

    /// <summary>Gets the deduplicated exchange-to-queue bindings.</summary>
    internal IReadOnlyList<ExchangeQueueBinding> ExchangeQueueBindings { get; }

    /// <summary>Gets the deduplicated exchange-to-exchange bindings.</summary>
    internal IReadOnlyList<ExchangeExchangeBinding> ExchangeExchangeBindings { get; }

    /// <summary>
    /// Returns whether an exchange named <paramref name="name"/> may be routed to: <see langword="true"/>
    /// for the predeclared default exchange (an empty name) or for any name present in
    /// <see cref="Exchanges"/>.
    /// </summary>
    internal bool ContainsExchange(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length == 0 || Exchanges.ContainsKey(name);
    }

    /// <summary>Returns whether a queue named <paramref name="name"/> is declared or was auto-declared.</summary>
    internal bool ContainsQueue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Queues.ContainsKey(name);
    }
}
