using System.Collections.Concurrent;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The in-memory message broker for a single dependency-injection container. Registered as a DI
/// singleton by <c>AddBareWireInMemory</c> — one instance per container, never a process-wide static
/// instance, so two containers in the same process (e.g. two hosted services, or two test fixtures)
/// never share queues.
/// </summary>
/// <remarks>
/// This subtask carries only the minimal queue-name registry needed to prove container isolation;
/// the actual bounded message queues, routing, and settlement are added by later subtasks. The
/// registry is populated exclusively by explicit calls (there is no path from inbound traffic to
/// <see cref="TryAddQueue"/> yet), so it stays bounded by the number of declared queues.
/// </remarks>
internal sealed class InMemoryBroker(InMemoryTransportOptions options)
{
    private readonly ConcurrentDictionary<string, byte> _queueNames = new(StringComparer.Ordinal);

    internal InMemoryTransportOptions Options { get; } = options;

    /// <summary>
    /// Registers a queue name with this broker instance, if not already present.
    /// </summary>
    /// <param name="name">The queue name. Must not be <see langword="null"/> or empty.</param>
    /// <returns><see langword="true"/> when the queue name was newly added; <see langword="false"/> when it already existed.</returns>
    internal bool TryAddQueue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _queueNames.TryAdd(name, 0);
    }

    /// <summary>
    /// Returns whether a queue with the given name is registered on this broker instance.
    /// </summary>
    /// <param name="name">The queue name. Must not be <see langword="null"/> or empty.</param>
    internal bool ContainsQueue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _queueNames.ContainsKey(name);
    }

    /// <summary>Gets the number of queue names currently registered on this broker instance.</summary>
    internal int QueueCount => _queueNames.Count;
}
