using System.Collections.Concurrent;
using System.Reflection;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;

namespace BareWire.Saga;

// Thread-safety strategy:
//   - ConcurrentDictionary for all structural operations (add/remove/enumerate).
//   - Per-key lock objects stored in a parallel ConcurrentDictionary<Guid, object> to
//     serialise the read-check-write sequence inside UpdateAsync without locking the
//     entire dictionary. This makes UpdateAsync atomic per correlation ID.
internal sealed class InMemorySagaRepository<TSaga> : ISagaRepository<TSaga>
    where TSaga : class, ISagaState, new()
{
    private readonly ConcurrentDictionary<Guid, TSaga> _store = new();

    // One lock object per saga key; created lazily on first use.
    private readonly ConcurrentDictionary<Guid, object> _locks = new();

    // Cached list of public, instance, settable properties used for deep copy.
    // Computed once per closed generic type and shared across all instances.
    private static readonly PropertyInfo[] _settableProperties =
        typeof(TSaga)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToArray();

    public Task<TSaga?> FindAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        if (!_store.TryGetValue(correlationId, out var stored))
        {
            return Task.FromResult<TSaga?>(null);
        }

        return Task.FromResult<TSaga?>(DeepCopy(stored));
    }

    public Task SaveAsync(TSaga saga, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saga);

        if (!_store.TryAdd(saga.CorrelationId, DeepCopy(saga)))
        {
            throw new InvalidOperationException(
                $"Saga with CorrelationId {saga.CorrelationId} already exists.");
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(TSaga saga, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saga);

        var lockObj = _locks.GetOrAdd(saga.CorrelationId, static _ => new object());

        lock (lockObj)
        {
            if (!_store.TryGetValue(saga.CorrelationId, out var stored))
            {
                // Saga was deleted between the executor's FindAsync and UpdateAsync;
                // treat it as a concurrency conflict so the executor can retry.
                throw new ConcurrencyException(
                    typeof(TSaga),
                    saga.CorrelationId,
                    expectedVersion: saga.Version,
                    actualVersion: -1,
                    currentState: saga.CurrentState);
            }

            if (saga.Version != stored.Version)
            {
                throw new ConcurrencyException(
                    typeof(TSaga),
                    saga.CorrelationId,
                    expectedVersion: saga.Version,
                    actualVersion: stored.Version,
                    currentState: stored.CurrentState);
            }

            saga.Version++;
            _store[saga.CorrelationId] = DeepCopy(saga);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        // No-op if the saga does not exist — idempotent by design.
        _store.TryRemove(correlationId, out _);
        _locks.TryRemove(correlationId, out _);

        return Task.CompletedTask;
    }

    // Creates a shallow-by-property copy: allocates a new TSaga instance and copies
    // all public settable properties. Sufficient for POCO saga states whose properties
    // are value types or immutable reference types (Guid, string, int, enums).
    private static TSaga DeepCopy(TSaga source)
    {
        var copy = new TSaga();
        foreach (var property in _settableProperties)
        {
            property.SetValue(copy, property.GetValue(source));
        }
        return copy;
    }
}
