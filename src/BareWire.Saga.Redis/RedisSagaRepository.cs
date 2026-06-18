using System.Globalization;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;
using StackExchange.Redis;

namespace BareWire.Saga.Redis;

/// <summary>
/// Redis-backed implementation of <see cref="ISagaRepository{TSaga}"/> using StackExchange.Redis.
/// </summary>
/// <typeparam name="TSaga">The SAGA state type. Must implement <see cref="ISagaState"/>.</typeparam>
/// <remarks>
/// <para>
/// SAGA state is stored as a Redis Hash with two fields: <c>state</c> (UTF-8 JSON bytes)
/// and <c>version</c> (integer). Optimistic concurrency is enforced server-side via Lua scripts,
/// guaranteeing atomic check-and-update in a single round trip without client-side locking.
/// </para>
/// <para>
/// This repository is identity-only: lookup is exclusively by <see cref="ISagaState.CorrelationId"/>.
/// It does not implement <c>IQueryableSagaRepository</c> because Redis does not support
/// arbitrary predicate queries over values.
/// </para>
/// </remarks>
internal sealed class RedisSagaRepository<TSaga> : ISagaRepository<TSaga>
    where TSaga : class, ISagaState
{
    private readonly IDatabase _database;
    private readonly RedisSagaRepositoryOptions _options;
    private readonly string _keyPrefix;

    /// <summary>
    /// Initializes a new instance of <see cref="RedisSagaRepository{TSaga}"/>.
    /// </summary>
    /// <param name="multiplexer">
    /// The StackExchange.Redis connection multiplexer. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="options">
    /// Repository options controlling key prefix and optional TTL. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="serializer">
    /// The serializer for converting SAGA state to and from Redis values. Must not be <see langword="null"/>.
    /// </param>
    internal RedisSagaRepository(
        IConnectionMultiplexer multiplexer,
        RedisSagaRepositoryOptions options,
        SagaStateSerializer<TSaga> serializer)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serializer);

        _database = multiplexer.GetDatabase();
        _options = options;
        _keyPrefix = string.IsNullOrEmpty(options.KeyPrefix)
            ? typeof(TSaga).Name
            : options.KeyPrefix;
    }

    /// <inheritdoc/>
    public async Task<TSaga?> FindAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(correlationId);
        RedisValue value = await _database.HashGetAsync(key, "state").ConfigureAwait(false);

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return SagaStateSerializer<TSaga>.Deserialize(value);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(TSaga saga, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saga);

        var key = BuildKey(saga.CorrelationId);
        RedisValue serialized = SagaStateSerializer<TSaga>.Serialize(saga);
        long ttlMs = GetTtlMilliseconds();

        RedisResult result = await _database.ScriptEvaluateAsync(
            RedisSagaScripts.SaveIfNotExists,
            keys: [key],
            values:
            [
                serialized,
                saga.Version.ToString(CultureInfo.InvariantCulture),
                ttlMs.ToString(CultureInfo.InvariantCulture)
            ]).ConfigureAwait(false);

        if ((long)result == 0)
        {
            throw new InvalidOperationException(
                $"A saga of type '{typeof(TSaga).Name}' with CorrelationId '{saga.CorrelationId}' already exists.");
        }
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(TSaga saga, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(saga);

        int expectedVersion = saga.Version;
        saga.Version++;

        var key = BuildKey(saga.CorrelationId);
        RedisValue serialized = SagaStateSerializer<TSaga>.Serialize(saga);
        long ttlMs = GetTtlMilliseconds();

        RedisResult result = await _database.ScriptEvaluateAsync(
            RedisSagaScripts.UpdateWithVersionGuard,
            keys: [key],
            values:
            [
                expectedVersion.ToString(CultureInfo.InvariantCulture),
                serialized,
                saga.Version.ToString(CultureInfo.InvariantCulture),
                ttlMs.ToString(CultureInfo.InvariantCulture)
            ]).ConfigureAwait(false);

        string resultStr = (string?)result ?? string.Empty;

        if (resultStr == "ok")
        {
            return;
        }

        // Restore version before throwing so the caller's object is left in a consistent state.
        saga.Version = expectedVersion;

        if (resultStr == "missing")
        {
            throw new ConcurrencyException(
                typeof(TSaga),
                saga.CorrelationId,
                expectedVersion,
                actualVersion: -1,
                saga.CurrentState);
        }

        if (resultStr.StartsWith("conflict:", StringComparison.Ordinal))
        {
            int actualVersion = int.TryParse(resultStr.AsSpan("conflict:".Length), out int parsed)
                ? parsed
                : -1;

            throw new ConcurrencyException(
                typeof(TSaga),
                saga.CorrelationId,
                expectedVersion,
                actualVersion,
                saga.CurrentState);
        }

        // Should never happen — defensive guard against unexpected Lua return values.
        throw new InvalidOperationException(
            $"Unexpected result from Redis update script for saga '{typeof(TSaga).Name}' " +
            $"(correlationId={saga.CorrelationId}): '{resultStr}'.");
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(correlationId);
        await _database.KeyDeleteAsync(key).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the Redis key for the given correlation identifier using the configured prefix.
    /// </summary>
    /// <param name="correlationId">The SAGA correlation identifier.</param>
    /// <returns>The Redis key string in the format <c>{KeyPrefix}:{correlationId:D}</c>.</returns>
    internal string BuildKey(Guid correlationId) => $"{_keyPrefix}:{correlationId:D}";

    private long GetTtlMilliseconds()
        => _options.StateTtl.HasValue
            ? (long)_options.StateTtl.Value.TotalMilliseconds
            : 0L;
}
