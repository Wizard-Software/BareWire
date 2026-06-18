namespace BareWire.Saga.Redis;

/// <summary>
/// Contains embedded Lua scripts used by <see cref="RedisSagaRepository{TSaga}"/> for
/// atomic Redis operations. Scripts are passed as compile-time constants; StackExchange.Redis
/// manages EVALSHA caching internally via its script-load mechanism.
/// </summary>
/// <remarks>
/// All scripts use parameterised <c>KEYS[]</c> and <c>ARGV[]</c> bindings. No user-supplied
/// data is ever concatenated into the script body, eliminating any Lua injection risk.
/// </remarks>
internal static class RedisSagaScripts
{
    /// <summary>
    /// Atomically creates a new SAGA Hash entry only when the key does not already exist.
    /// </summary>
    /// <remarks>
    /// <para>Parameters:</para>
    /// <list type="bullet">
    ///   <item><description><c>KEYS[1]</c> — the Redis key for the SAGA entry.</description></item>
    ///   <item><description><c>ARGV[1]</c> — the serialised SAGA state as UTF-8 JSON bytes.</description></item>
    ///   <item><description><c>ARGV[2]</c> — the initial version as a string integer (typically "0").</description></item>
    ///   <item><description><c>ARGV[3]</c> — TTL in milliseconds as a string integer, or "0" for no expiry.</description></item>
    /// </list>
    /// <para>Returns:</para>
    /// <list type="bullet">
    ///   <item><description><c>1</c> — entry created successfully.</description></item>
    ///   <item><description><c>0</c> — key already exists; entry was NOT created.</description></item>
    /// </list>
    /// </remarks>
    internal static readonly string SaveIfNotExists = """
        if redis.call('EXISTS', KEYS[1]) == 1 then
            return 0
        end
        redis.call('HSET', KEYS[1], 'state', ARGV[1], 'version', ARGV[2])
        if tonumber(ARGV[3]) > 0 then
            redis.call('PEXPIRE', KEYS[1], ARGV[3])
        end
        return 1
        """;

    /// <summary>
    /// Atomically updates an existing SAGA Hash entry with optimistic concurrency control.
    /// Compares the stored <c>version</c> field against the expected version before writing.
    /// </summary>
    /// <remarks>
    /// <para>Parameters:</para>
    /// <list type="bullet">
    ///   <item><description><c>KEYS[1]</c> — the Redis key for the SAGA entry.</description></item>
    ///   <item><description><c>ARGV[1]</c> — the expected (current) version as a string integer.</description></item>
    ///   <item><description><c>ARGV[2]</c> — the new serialised SAGA state as UTF-8 JSON bytes.</description></item>
    ///   <item><description><c>ARGV[3]</c> — the new version as a string integer (expected version + 1).</description></item>
    ///   <item><description><c>ARGV[4]</c> — TTL in milliseconds as a string integer, or "0" for no expiry.</description></item>
    /// </list>
    /// <para>Returns:</para>
    /// <list type="bullet">
    ///   <item><description><c>"ok"</c> — update succeeded.</description></item>
    ///   <item><description><c>"missing"</c> — the key does not exist (SAGA was deleted concurrently).</description></item>
    ///   <item><description><c>"conflict:{actualVersion}"</c> — stored version does not match expected; includes actual version.</description></item>
    /// </list>
    /// </remarks>
    internal static readonly string UpdateWithVersionGuard = """
        local storedVersion = redis.call('HGET', KEYS[1], 'version')
        if storedVersion == false then
            return 'missing'
        end
        if tonumber(storedVersion) ~= tonumber(ARGV[1]) then
            return 'conflict:' .. storedVersion
        end
        redis.call('HSET', KEYS[1], 'state', ARGV[2], 'version', ARGV[3])
        if tonumber(ARGV[4]) > 0 then
            redis.call('PEXPIRE', KEYS[1], ARGV[4])
        end
        return 'ok'
        """;
}
