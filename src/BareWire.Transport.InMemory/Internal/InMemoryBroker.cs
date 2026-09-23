using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The in-memory message broker for a single dependency-injection container. Registered as a DI
/// singleton by <c>AddBareWireInMemory</c> — one instance per container, never a process-wide static
/// instance, so two containers in the same process (e.g. two hosted services, or two test fixtures)
/// never share queues.
/// </summary>
/// <remarks>
/// Queues originate exclusively from the sealed topology registry attached via
/// <see cref="AttachRegistry"/> — one <see cref="InMemoryQueue"/> per <see cref="ExchangeRegistry.Queues"/>
/// entry, built once when the owning <see cref="InMemoryTransportAdapter"/> is constructed and published
/// atomically as a single immutable state. Inbound traffic never creates a queue: there is no other path
/// that adds an entry to this broker.
/// </remarks>
internal sealed class InMemoryBroker(InMemoryTransportOptions options)
{
    private BrokerState? _state;

    internal InMemoryTransportOptions Options { get; } = options;

    /// <summary>
    /// Builds one <see cref="InMemoryQueue"/> per queue declared in <paramref name="registry"/>, with a
    /// capacity of <see cref="InMemoryTransportOptions.QueueCapacity"/>, and publishes the registry and
    /// the resulting queue dictionary together as a single immutable state with one atomic compare-and-swap
    /// — there is no window in which one is visible without the other. Attaching the same
    /// <paramref name="registry"/> instance again is a no-op; attaching a different one throws, because a
    /// broker binds to exactly one topology registry for its lifetime (two adapters sharing one broker is
    /// a composition error).
    /// </summary>
    /// <param name="registry">The sealed topology registry to create queues from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// This broker is already attached to a different topology registry.
    /// </exception>
    internal void AttachRegistry(ExchangeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        BrokerState? existing = Volatile.Read(ref _state);
        if (existing is not null && ReferenceEquals(existing.Registry, registry))
        {
            return;
        }

        FrozenDictionary<string, InMemoryQueue> queues = registry.Queues.Keys.ToFrozenDictionary(
            static name => name,
            name => new InMemoryQueue(name, Options.QueueCapacity),
            StringComparer.Ordinal);
        var built = new BrokerState(registry, queues);

        BrokerState? previous = Interlocked.CompareExchange(ref _state, built, null);
        if (previous is null || ReferenceEquals(previous.Registry, registry))
        {
            return;
        }

        throw new InvalidOperationException(
            "The in-memory broker is already bound to a different topology registry.");
    }

    /// <summary>
    /// Attempts to get the queue named <paramref name="name"/>, if this broker has been attached to a
    /// topology registry that declares it.
    /// </summary>
    /// <param name="name">The queue name. Must not be <see langword="null"/> or empty.</param>
    /// <param name="queue">The queue, when found.</param>
    internal bool TryGetQueue(string name, [NotNullWhen(true)] out InMemoryQueue? queue)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        FrozenDictionary<string, InMemoryQueue>? queues = Volatile.Read(ref _state)?.Queues;
        if (queues is null)
        {
            queue = null;
            return false;
        }

        return queues.TryGetValue(name, out queue);
    }

    /// <summary>
    /// Returns whether a queue with the given name is registered on this broker instance.
    /// </summary>
    /// <param name="name">The queue name. Must not be <see langword="null"/> or empty.</param>
    internal bool ContainsQueue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Volatile.Read(ref _state)?.Queues.ContainsKey(name) ?? false;
    }

    /// <summary>Gets the number of queues currently registered on this broker instance.</summary>
    internal int QueueCount => Volatile.Read(ref _state)?.Queues.Count ?? 0;

    private sealed record BrokerState(ExchangeRegistry Registry, FrozenDictionary<string, InMemoryQueue> Queues);
}
